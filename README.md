# Octsock5_cSharp - high speed inter-process data interface #

C# counterpart to Julia octsock5 at https://github.com/mnentwig/octsock5.jl

The included standalone program acts as loopback client that echoes all received data back. Instructions for testing with the Julia end are shown on-screen.


## Performance on reference system ## 
All numbers are roundtrip.

Throughput: 811 MBytes / second

latency: 22 microseconds

Note: At the time of writing, the above C# performance is lagging behind the Julia implementation by about a factor of 2.
