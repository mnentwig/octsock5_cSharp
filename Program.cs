using System;
using octsock5;
class octsockLoopbackServerDemo {
    static void Main(string[] args) {
        Console.WriteLine("SERVER_READY"); // agreed token via STDOUT for automated startup
        Console.WriteLine("octsock5 C# loopback client, echoes back all received data.");
        Console.WriteLine("Now run e.g.");
        Console.WriteLine("\tjulia.exe test/main.jl client helloworld");
        Console.WriteLine("to connect");
        octsock5_cl os = new octsock5_cl(portId :-12345, isServer :true);

        int count = 0;
        while(true) {
            dynamic o = os.read();
            os.write(o);
            if(o is string)
                if(o as string == "end loopback and have a nice day")
                    break;
            ++count;
            if(count % 10000 == 0)
                Console.WriteLine("handled "+ count + " top level transactions");
        }
        os.Dispose();
        Console.WriteLine("OK: Server signalled regular shutdown. Press RETURN to close");
        Console.ReadLine();
    }
}
