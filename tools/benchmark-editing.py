"""Compare editing bookkeeping with HEAD using isolated build directories."""
from pathlib import Path
import subprocess
import json
ROOT = Path(__file__).resolve().parents[1]
program = r"""
using System.Diagnostics;
using System.Text.Json;
using AdTrim.Models;
using AdTrim.Commands;
using AdTrim.ViewModels;
var vm = new MainViewModel { DurationUs = 3_000_000_000 };
for (int i=0;i<=100;i++) vm.Markers.Add(new Split { TimeUs=i*30_000_000L, Label=i==0?"Start":i==100?"End":null });
vm.RebuildSegmentsFromSplits();
var marker=vm.Markers[50];
void Move() { var command=new MoveSplitCommand(vm,marker,marker.TimeUs,marker.TimeUs+33_333); command.Do(); command.Undo(); }
for(int i=0;i<100;i++) Move();
var samples=new double[1000];
long before=GC.GetTotalAllocatedBytes(true);
for(int i=0;i<samples.Length;i++) { long start=Stopwatch.GetTimestamp(); Move(); samples[i]=Stopwatch.GetElapsedTime(start).TotalMilliseconds; }
long allocated=GC.GetTotalAllocatedBytes(true)-before;
Array.Sort(samples);
long begin=Stopwatch.GetTimestamp();
for(int i=0;i<100000;i++) vm.PlayheadUs=i;
Console.WriteLine(JsonSerializer.Serialize(new { move_undo_median_ms=samples[500], move_undo_p95_ms=samples[950], allocated_bytes_per_pair=allocated/1000, playhead_100000_ms=Stopwatch.GetElapsedTime(begin).TotalMilliseconds }));
"""
for version in ["baseline", "current"]:
    directory=ROOT/"src/AdTrim/obj/edit-benchmark"/version
    directory.mkdir(parents=True,exist_ok=True)
    files=list((ROOT/"src/AdTrim/Models").glob("*.cs"))+list((ROOT/"src/AdTrim/Commands").glob("*.cs"))+[ROOT/"src/AdTrim/ViewModels/MainViewModel.cs"]
    for file in files:
        relative=file.relative_to(ROOT).as_posix()
        if version=="baseline":
            result=subprocess.run(["git","show","HEAD:"+relative],cwd=ROOT,capture_output=True)
            if result.returncode: continue
            data=result.stdout
        else: data=file.read_bytes()
        (directory/file.name).write_bytes(data)
    (directory/"Program.cs").write_text(program)
    (directory/"Benchmark.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>')
    result=subprocess.run(["dotnet","run","--project",str(directory),"-c","Release"],cwd=ROOT,text=True,capture_output=True,check=True)
    print(version+": "+result.stdout.strip())
