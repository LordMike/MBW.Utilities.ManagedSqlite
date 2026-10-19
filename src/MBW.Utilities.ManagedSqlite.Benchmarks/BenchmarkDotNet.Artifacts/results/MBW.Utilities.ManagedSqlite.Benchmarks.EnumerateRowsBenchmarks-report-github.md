```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.112
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=100ms  
LaunchCount=1  WarmupCount=2  Categories=EnumerateRows  

```
| Method  | Rows   | Mean             | Error            | StdDev           | Gen0      | Allocated   |
|-------- |------- |-----------------:|-----------------:|-----------------:|----------:|------------:|
| **Managed** | **0**      |         **80.03 ns** |         **41.02 ns** |         **10.65 ns** |    **0.0086** |       **152 B** |
|         |        |                  |                  |                  |           |             |
| **Managed** | **1**      |        **766.44 ns** |        **158.31 ns** |         **41.11 ns** |    **0.0699** |      **1328 B** |
|         |        |                  |                  |                  |           |             |
| **Managed** | **1000**   |    **767,237.58 ns** |    **705,778.66 ns** |    **109,220.03 ns** |   **50.0000** |   **1007373 B** |
|         |        |                  |                  |                  |           |             |
| **Managed** | **100000** | **96,248,750.00 ns** | **75,584,419.40 ns** | **19,629,033.73 ns** | **6000.0000** | **104962160 B** |
