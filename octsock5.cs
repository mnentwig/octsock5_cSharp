using System;
using System.IO;
using System.IO.Pipes;
using System.Collections.Generic;
using System.Runtime.InteropServices;

// Note http://code4k.blogspot.de/2010/10/high-performance-memcpy-gotchas-in-c.html
// conclusion: x64 BlockCopy performs almost as good as memcpy

namespace octsock5 {
    // C# has no equivalent for non-double complex types. 
    // Therefore, keep real and use flag to denote interleaved complex
    /// <summary>
    /// Helper class for matrix data
    /// C# lacks support for generic complex types (e.g. uint8, int8) 2+ -dimensional matrices
    /// This helper class preserves data in the original memory layout in a one-dimensional array of the base type.
    /// </summary>
    /// <typeparam name="T">base type of data</typeparam>
    public class octsock5Matrix<T> {
        /// <summary>
        /// Dimensions of matrix data. Null for scalars
        /// </summary>
        public Int64[] dims;
        /// <summary>
        /// Data following the original memory layout (column major, interleaved if complex)
        /// </summary>
        public T[] colMajorData;
        /// <summary>
        /// flag that identifies data represents complex interleaved
        /// </summary>
        public bool interleavedComplex;

        public octsock5Matrix() { }
        public octsock5Matrix(T scalar) {
            this.colMajorData = new T[1];
            this.colMajorData[0] = scalar;
        }

        public octsock5Matrix(T scalarReal, T scalarImag) {
            this.colMajorData = new T[2];
            this.colMajorData[0] = scalarReal;
            this.colMajorData[1] = scalarImag;
            this.interleavedComplex = true;
        }
    }

    public class octsock5_cl:IDisposable {
        const int nHeaderBytes = 6*sizeof(Int64);

        const Int64 H0_FLOAT    = 0x00000001;
        const Int64 H0_INTEGER  = 0x00000002;
        const Int64 H0_SIGNED   = 0x00000004;
        const Int64 H0_COMPLEX  = 0x00000008;
        const Int64 H0_ARRAY    = 0x00000010;
        const Int64 H0_TUPLE    = 0x00000020;
        const Int64 H0_STRING   = 0x00000040;
        const Int64 H0_DICT     = 0x00000080;
        const Int64 H0_TERM     = 0x00000100;

        private byte[] buf = new byte[1000];
        private Int64[] header = new Int64[6];
        private Stream io;
        private StreamReader ioReader;
        private StreamWriter ioWriter;
        public octsock5_cl(Int64 portId, bool isServer) {
            // name of the "Windows named pipe". Agreed between different implementations
            string pipename = "octsock5_"+portId.ToString();
            if(isServer) {
                NamedPipeServerStream server = new NamedPipeServerStream(pipename);
                this.io = server;
                server.WaitForConnection();
            }
            else {
                NamedPipeClientStream client = new NamedPipeClientStream(pipename);
                this.io = client;
                client.Connect();
            }

            this.ioReader = new StreamReader(this.io);
            this.ioWriter = new StreamWriter(this.io);
        }

        /// <summary>
        /// reads the next inbound object
        /// </summary>
        /// <returns>any supported type</returns>
        public object read() {
            // === read header ===
            if(nHeaderBytes != this.ioReader.BaseStream.Read(this.buf, 0, nHeaderBytes)) throw new Exception("read failed (pipe closed?)");
            Int64 h0 = BitConverter.ToInt64(this.buf, 0*sizeof(Int64));
            Int64 h1 = BitConverter.ToInt64(this.buf, 1*sizeof(Int64));

            // === handle dict terminator ===
            if((h0 & H0_TERM) != 0)
                return null;

            // === handle tuple ===
            if((h0 & H0_TUPLE) != 0) {
                object[] retVal = new object[h1];
                for(int ix = 0; ix < h1; ++ix)
                    retVal[ix] = this.read();
                return retVal;
            }

            // === handle string ===            
            if((h0 & H0_STRING) != 0) {
                // === extend temp string buffer ===
                this.readToBuf(h1);
                return System.Text.Encoding.ASCII.GetString(buf, 0, (Int32)h1);
            }

            // === handle dict ===            
            if((h0 & H0_DICT) != 0) {
                Dictionary<object, object> retVal = new Dictionary<object, object>();
                while(true) {
                    object key = this.read();
                    if(key == null)
                        return retVal;
                    object val = this.read();
                    retVal[key] = val;
                }
            }

            // === handle array or scalar ===            
            // Construct a code uniquely identifying each type, with h1 being the element size
            // Note: The element size of complex types is real+imag, but we handle real data => 2*size << 16 is equivalent to size << 17
            switch((h0 & ~H0_ARRAY) | (h1 << 16)) {
                case (H0_FLOAT                              | (4 << 16)): return this.readMatrixOrScalar<float>();
                case (H0_FLOAT                              | (8 << 16)): return this.readMatrixOrScalar<double>();
                case (H0_FLOAT              | H0_COMPLEX    | (4 << 17)): return this.readMatrixOrScalar<float>(interleavedComplex :true);
                case (H0_FLOAT              | H0_COMPLEX    | (8 << 17)): return this.readMatrixOrScalar<double>(interleavedComplex :true);
                case (H0_INTEGER                            | (1 << 16)): return this.readMatrixOrScalar<byte>();
                case (H0_INTEGER | H0_SIGNED                | (1 << 16)): return this.readMatrixOrScalar<sbyte>();
                case (H0_INTEGER                            | (2 << 16)): return this.readMatrixOrScalar<UInt16>();
                case (H0_INTEGER | H0_SIGNED                | (2 << 16)): return this.readMatrixOrScalar<Int16>();
                case (H0_INTEGER                            | (4 << 16)): return this.readMatrixOrScalar<UInt32>();
                case (H0_INTEGER | H0_SIGNED                | (4 << 16)): return this.readMatrixOrScalar<Int32>();
                case (H0_INTEGER                            | (8 << 16)): return this.readMatrixOrScalar<UInt64>();
                case (H0_INTEGER | H0_SIGNED                | (8 << 16)): return this.readMatrixOrScalar<Int64>();
                case (H0_INTEGER              | H0_COMPLEX  | (1 << 17)): return this.readMatrixOrScalar<byte>(interleavedComplex :true);
                case (H0_INTEGER | H0_SIGNED  | H0_COMPLEX  | (1 << 17)): return this.readMatrixOrScalar<sbyte>(interleavedComplex :true);
                case (H0_INTEGER              | H0_COMPLEX  | (2 << 17)): return this.readMatrixOrScalar<UInt16>(interleavedComplex :true);
                case (H0_INTEGER | H0_SIGNED  | H0_COMPLEX  | (2 << 17)): return this.readMatrixOrScalar<Int16>(interleavedComplex :true);
                case (H0_INTEGER              | H0_COMPLEX  | (4 << 17)): return this.readMatrixOrScalar<UInt32>(interleavedComplex :true);
                case (H0_INTEGER | H0_SIGNED  | H0_COMPLEX  | (4 << 17)): return this.readMatrixOrScalar<Int32>(interleavedComplex :true);
                case (H0_INTEGER              | H0_COMPLEX  | (8 << 17)): return this.readMatrixOrScalar<UInt64>(interleavedComplex :true);
                case (H0_INTEGER | H0_SIGNED  | H0_COMPLEX  | (8 << 17)): return this.readMatrixOrScalar<Int64>(interleavedComplex :true);
                default: throw new Exception("read: unsupported header");
            }
        }

        /// <summary>
        /// helper function, extends internal buffer and reads into it
        /// </summary>
        /// <param name="n">number of bytes to read</param>
        private void readToBuf(Int64 n) {
            if(n > this.buf.Length)
                this.buf = new byte[Math.Max(n, 2*this.buf.Length)];
            if(n != this.ioReader.BaseStream.Read(buf, 0, (Int32)n)) throw new Exception("read failed (pipe closed?)");
        }

        /// <summary>
        /// reads octsock5Matrix<typeparamref name="T"/>, once the header has been processed
        /// </summary>
        /// <typeparam name="T">type of inbound data (known from header)</typeparam>
        /// <param name="interleavedComplex">whether inbound data represents complex data</param>
        /// <returns>octsock5Matrix<typeparamref name="T"/></returns>
        private object readMatrixOrScalar<T>(bool interleavedComplex = false) {
            Int64 h0 = BitConverter.ToInt64(this.buf, 0*sizeof(Int64)); // ID
            Int64 h1 = BitConverter.ToInt64(this.buf, 1*sizeof(Int64)); // nBytes/elem
            Int64 h2 = BitConverter.ToInt64(this.buf, 2*sizeof(Int64)); // nDims

            // === determine dimensions ===
            Int64[] dims;
            Int64 nElems = 1;
            bool isArray = ((h0 & H0_ARRAY) != 0);
            if(isArray) {
                dims = new Int64[h2];
                Buffer.BlockCopy(this.buf, 3*sizeof(Int64), dims, 0, (int)h2*sizeof(Int64));
                for(int ix = 0; ix < dims.Length; ++ix)
                    nElems *= dims[ix];
            }
            else
                dims = new Int64[] { }; // empty dims flags scalar

            // === create new matrix object ===
            octsock5Matrix<T> tmp = new octsock5Matrix<T>();
            tmp.dims = dims;
            Int64 nBytesTotal = nElems*h1; // note: for complex, h1 is already doubled

            if(interleavedComplex) nElems *= 2; // for interleaved complex, double the number of real elements
            tmp.colMajorData = new T[nElems];
            tmp.interleavedComplex = interleavedComplex;

            // === read data ===
            this.readToBuf(nBytesTotal);

            // === copy data ===
            Buffer.BlockCopy(this.buf, 0, tmp.colMajorData, 0, (int)nBytesTotal);

            // === return array or unwrap scalar ===
            // Note: C# has no 
            if(isArray || interleavedComplex) return tmp;
            return tmp.colMajorData[0];
        }

        /// <summary>
        /// helper function, writes header from internal header buffer to stream
        /// </summary>
        protected void sendHeader() {
            Buffer.BlockCopy(this.header, 0, this.buf, 0, nHeaderBytes);
            this.ioWriter.BaseStream.Write(this.buf, 0, nHeaderBytes);
        }

        /// <summary>
        /// helper function, writes binary content of any array
        /// </summary>
        /// <typeparam name="T">type of array</typeparam>
        /// <param name="src">array to send</param>
        private void sendArray<T>(T[] src) {
            int nBytes = Marshal.SizeOf(typeof(T))*src.Length;

            // === provide sufficient buffer ===
            if(nBytes > this.buf.Length)
                this.buf = new byte[Math.Max(nBytes, 2*this.buf.Length)];

            // === copy data body to buffer ===
            Buffer.BlockCopy(src, 0, this.buf, 0, nBytes);

            // === send ===
            this.ioWriter.BaseStream.Write(this.buf, 0, nBytes);
        }

        /// <summary>
        /// Writes array as Tuple (vector of no specific type)
        /// NOTE: For sending true matrix data, wrap (numeric array, corresponding dimension vector, complex flag) into octsock5Matrix
        /// </summary>
        /// <typeparam name="T">type of array, can be object</typeparam>
        /// <param name="o">array to send</param>
        public void writeTuple<T>(T[] o) {
            this.header[0] = H0_TUPLE;
            this.header[1] = o.Length;
            this.sendHeader();
            for(int ix = 0; ix < o.Length; ++ix)
                this.write(o[ix]);
        }

        /// <summary>
        /// Writes a wrapped matrix type
        /// </summary>
        /// <typeparam name="T">type of numeric array</typeparam>
        /// <param name="o">wrapper object</param>
        public void writeWrapped<T>(octsock5Matrix<T> o) {
            int nElemBytes = Marshal.SizeOf(typeof(T));
            // === set flags ===
            if(typeof(T) == typeof(float)) this.header[0] = H0_FLOAT;
            else if(typeof(T) == typeof(double)) this.header[0] = H0_FLOAT;
            else if(typeof(T) == typeof(byte)) this.header[0] = H0_INTEGER;
            else if(typeof(T) == typeof(sbyte)) this.header[0] = H0_INTEGER | H0_SIGNED;
            else if(typeof(T) == typeof(UInt16)) this.header[0] = H0_INTEGER;
            else if(typeof(T) == typeof(Int16)) this.header[0] = H0_INTEGER | H0_SIGNED;
            else if(typeof(T) == typeof(UInt32)) this.header[0] = H0_INTEGER;
            else if(typeof(T) == typeof(Int32)) this.header[0] = H0_INTEGER | H0_SIGNED;
            else if(typeof(T) == typeof(UInt64)) this.header[0] = H0_INTEGER;
            else if(typeof(T) == typeof(Int64)) this.header[0] = H0_INTEGER | H0_SIGNED;
            else throw new Exception("unsupported type");

            // === set element size ===
            if(o.interleavedComplex) {
                this.header[0] |= H0_COMPLEX;
                this.header[1] = 2*nElemBytes;
            }
            else
                this.header[1] = nElemBytes;

            // === set dims ===
            if(o.dims == null || o.dims.Length == 0) {
                this.header[2] = 1; // one dimension
                this.header[3] = 1; // with size 1
            }
            else {
                this.header[2] = o.dims.Length;
                Buffer.BlockCopy(o.dims, 0, this.header, 3*sizeof(Int64), o.dims.Length * sizeof(Int64));
                this.header[0] |= H0_ARRAY;
            }

            this.sendHeader();
            this.sendArray(o.colMajorData);
        }

        /// <summary>
        /// Writes an ASCII string
        /// </summary>
        /// <param name="s">string to write</param>
        public void writeString(string s) {
            this.header[0] = H0_STRING;
            this.header[1] = s.Length;
            this.sendHeader();
            this.sendArray(System.Text.Encoding.ASCII.GetBytes(s));
        }

        /// <summary>
        /// Write data of any type. Note, consider using type-specific function for performance if the type is known at compile time
        /// </summary>
        /// <param name="o">object, any supported type</param>
        public void write(dynamic o) {
            // === wrap scalars ===
            if(o is float) o = new octsock5Matrix<float>(o);
            else if(o is double) o = new octsock5Matrix<double>(o);
            else if(o is byte) o = new octsock5Matrix<byte>(o);
            else if(o is sbyte) o = new octsock5Matrix<sbyte>(o);
            else if(o is UInt16) o = new octsock5Matrix<UInt16>(o);
            else if(o is Int16) o = new octsock5Matrix<Int16>(o);
            else if(o is UInt32) o = new octsock5Matrix<UInt32>(o);
            else if(o is Int32) o = new octsock5Matrix<Int32>(o);
            else if(o is UInt64) o = new octsock5Matrix<UInt64>(o);
            else if(o is Int64) o = new octsock5Matrix<Int64>(o);

            if(o is octsock5Matrix<float>) this.writeWrapped(o as octsock5Matrix<float>);
            else if(o is octsock5Matrix<double>) this.writeWrapped(o as octsock5Matrix<double>);
            else if(o is octsock5Matrix<byte>) this.writeWrapped(o as octsock5Matrix<byte>);
            else if(o is octsock5Matrix<sbyte>) this.writeWrapped(o as octsock5Matrix<sbyte>);
            else if(o is octsock5Matrix<UInt16>) this.writeWrapped(o as octsock5Matrix<UInt16>);
            else if(o is octsock5Matrix<Int16>) this.writeWrapped(o as octsock5Matrix<Int16>);
            else if(o is octsock5Matrix<UInt32>) this.writeWrapped(o as octsock5Matrix<UInt32>);
            else if(o is octsock5Matrix<Int32>) this.writeWrapped(o as octsock5Matrix<Int32>);
            else if(o is octsock5Matrix<UInt64>) this.writeWrapped(o as octsock5Matrix<UInt64>);
            else if(o is octsock5Matrix<Int64>) this.writeWrapped(o as octsock5Matrix<Int64>);
            else if(o is string) this.writeString(o as string);
            else if(o is Dictionary<object, object>) this.writeDict(o);
            else if(o is Array) this.writeTuple(o);
            else throw new Exception("unsupported type");
        }

        /// <summary>
        /// writes dictionary <object, object>
        /// </summary>
        /// <param name="d">dictionary to write</param>
        public void writeDict(Dictionary<object, object> d) {
            // === write dict header ===
            this.header[0] = H0_DICT;
            this.sendHeader();

            // === write key/value pairs ===
            foreach(object k in d.Keys) {
                this.write(k);
                this.write(d[k]);
            }

            // === write terminator ===
            this.header[0] = H0_TERM;
            this.sendHeader();
        }

        /// <summary>
        /// Closes the connection
        /// </summary>
        public void Dispose() {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing) {
            if(disposing)
                this.io.Close();
        }
    } // class octsock5_cl
} // namespace