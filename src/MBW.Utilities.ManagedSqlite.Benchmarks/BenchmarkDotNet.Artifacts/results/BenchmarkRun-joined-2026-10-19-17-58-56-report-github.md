```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.80GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.112
  [Host] : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v4

Toolchain=InProcessEmitToolchain  IterationCount=5  IterationTime=100ms  
LaunchCount=1  WarmupCount=2  

```
| Type                    | Method            | Categories    | Rows   | Mean              | Error             | StdDev            | Ratio | RatioSD | Gen0      | Allocated   | Alloc Ratio |
|------------------------ |------------------ |-------------- |------- |------------------:|------------------:|------------------:|------:|--------:|----------:|------------:|------------:|
| **EnumerateRowsBenchmarks** | **Native**            | **EnumerateRows** | **0**      |         **730.14 ns** |         **69.787 ns** |         **10.800 ns** |  **1.00** |    **0.02** |    **0.0152** |       **384 B** |        **1.00** |
| EnumerateRowsBenchmarks | Managed           | EnumerateRows | 0      |          57.07 ns |         14.114 ns |          2.184 ns |  0.08 |    0.00 |    0.0085 |       152 B |        0.40 |
|                         |                   |               |        |                   |                   |                   |       |         |           |             |             |
| **EnumerateRowsBenchmarks** | **Native**            | **EnumerateRows** | **1**      |       **2,184.03 ns** |        **101.101 ns** |         **15.646 ns** |  **1.00** |    **0.01** |    **0.0226** |       **544 B** |        **1.00** |
| EnumerateRowsBenchmarks | Managed           | EnumerateRows | 1      |         798.03 ns |        423.084 ns |         65.473 ns |  0.37 |    0.03 |    0.0770 |      1328 B |        2.44 |
|                         |                   |               |        |                   |                   |                   |       |         |           |             |             |
| **EnumerateRowsBenchmarks** | **Native**            | **EnumerateRows** | **1000**   |   **1,470,532.70 ns** |    **379,618.910 ns** |     **98,585.826 ns** |  **1.00** |    **0.09** |         **-** |    **167633 B** |        **1.00** |
| EnumerateRowsBenchmarks | Managed           | EnumerateRows | 1000   |     709,075.68 ns |    310,945.182 ns |     80,751.476 ns |  0.48 |    0.06 |   53.5714 |   1007366 B |        6.01 |
|                         |                   |               |        |                   |                   |                   |       |         |           |             |             |
| **EnumerateRowsBenchmarks** | **Native**            | **EnumerateRows** | **100000** | **148,326,377.00 ns** | **97,095,493.830 ns** | **15,025,635.123 ns** |  **1.01** |    **0.12** |         **-** |  **16802904 B** |        **1.00** |
| EnumerateRowsBenchmarks | Managed           | EnumerateRows | 100000 |  95,793,422.40 ns | 73,145,096.595 ns | 18,995,549.345 ns |  0.65 |    0.13 | 6000.0000 | 104962496 B |        6.25 |
|                         |                   |               |        |                   |                   |                   |       |         |           |             |             |
| **SchemaBenchmarks**        | **NativeListTables**  | **ListTables**    | **?**      |      **14,008.49 ns** |     **11,020.448 ns** |      **1,705.427 ns** | **1.013** |    **0.17** |         **-** |      **2601 B** |        **1.00** |
| SchemaBenchmarks        | ManagedListTables | ListTables    | ?      |          26.31 ns |          9.913 ns |          2.574 ns | 0.002 |    0.00 |         - |           - |        0.00 |
|                         |                   |               |        |                   |                   |                   |       |         |           |             |             |
| OpenDatabaseBenchmarks  | Native            | OpenDatabase  | ?      |      36,517.66 ns |      5,158.331 ns |      1,339.602 ns |  1.00 |    0.05 |         - |       361 B |        1.00 |
| OpenDatabaseBenchmarks  | Managed           | OpenDatabase  | ?      |      11,904.05 ns |      9,237.749 ns |      2,399.014 ns |  0.33 |    0.06 |    0.6706 |     12385 B |       34.31 |
|                         |                   |               |        |                   |                   |                   |       |         |           |             |             |
| SchemaBenchmarks        | NativeOpenTable   | OpenTable     | ?      |       1,137.87 ns |        843.373 ns |        219.021 ns |  1.03 |    0.26 |    0.0171 |       384 B |        1.00 |
| SchemaBenchmarks        | ManagedOpenTable  | OpenTable     | ?      |         616.83 ns |        451.238 ns |        117.185 ns |  0.56 |    0.14 |    0.0160 |       352 B |        0.92 |
