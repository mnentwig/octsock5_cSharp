using System;
using octsock5;
class octsockLoopbackServerDemo {
    static void Main(string[] args) {
        octsock5_cl os = new octsock5_cl(portId :"-12345", isServer :true);
        Console.WriteLine("SERVER_READY"); // agreed token via STDOUT for automated startup
        os.accept();

        while(true) {
            dynamic o = os.read();
            os.write(o);
            if(o is string)
                if(o as string == "end loopback and have a nice day")
                    break;
        }
        os.Dispose();
        Console.WriteLine("SERVER_EXIT");
    }
}
