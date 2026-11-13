```

BenchmarkDotNet v0.15.8, Windows 11 (10.0.26300.9818)
11th Gen Intel Core i5-11400H 2.70GHz (Max: 2.69GHz), 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.403
  [Host]     : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4
  Job-CNUJVU : .NET 10.0.14 (10.0.14, 10.0.1426.52326), X64 RyuJIT x86-64-v4

InvocationCount=1  UnrollFactor=1  

```
| Method                                | ItemCount | Mean    | Error   | StdDev  | Ratio | RatioSD | Alloc Ratio |
|-------------------------------------- |---------- |--------:|--------:|--------:|------:|--------:|------------:|
| &#39;EF Core SaveChanges&#39;                 | 1000000   | 20.02 s | 0.261 s | 0.204 s |  1.00 |    0.01 |          NA |
| &#39;DbContextBatchSave SaveAddedChanges&#39; | 1000000   | 20.34 s | 0.353 s | 0.447 s |  1.02 |    0.02 |          NA |
