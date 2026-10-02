"""Exercise bundled mpv on synthetic cuts without opening a window or real recordings."""
import ctypes as c
import hashlib
import json
import math
from pathlib import Path
import statistics
import struct
import subprocess
import time
import uuid
import shutil
from contextlib import contextmanager

@contextmanager
def probe_directory():
    parent = (ROOT / "src/AdTrim/obj").resolve()
    directory = parent / ("playback-" + uuid.uuid4().hex)
    directory.mkdir()
    try: yield directory
    finally:
        if directory.resolve().parent != parent: raise RuntimeError("Invalid cleanup directory")
        shutil.rmtree(directory)

ROOT = Path(__file__).resolve().parents[1]
class Event(c.Structure):
    _fields_ = [("id", c.c_int), ("error", c.c_int), ("reply", c.c_uint64), ("data", c.c_void_p)]
mpv = c.CDLL(str(ROOT / "binaries/mpv/win-x64/libmpv-2.dll"))
mpv.mpv_create.restype = c.c_void_p
mpv.mpv_initialize.argtypes = [c.c_void_p]
mpv.mpv_set_option_string.argtypes = [c.c_void_p, c.c_char_p, c.c_char_p]
mpv.mpv_command.argtypes = [c.c_void_p, c.POINTER(c.c_char_p)]
mpv.mpv_get_property.argtypes = [c.c_void_p, c.c_char_p, c.c_int, c.c_void_p]
mpv.mpv_wait_event.argtypes = [c.c_void_p, c.c_double]
mpv.mpv_wait_event.restype = c.POINTER(Event)
mpv.mpv_terminate_destroy.argtypes = [c.c_void_p]

def run(source, capture=None, hardware="no"):
    ctx = mpv.mpv_create()
    options = {"vo": "null", "ao": "pcm" if capture else "null", "hwdec": hardware,
        "terminal": "no", "idle": "yes", "keep-open": "no", "cache": "yes",
        "demuxer-max-bytes": "33554432", "demuxer-readahead-secs": "2", "audio-format": "s16",
        "audio-samplerate": "48000", "audio-channels": "mono"}
    if capture: options.update({"ao-pcm-file": str(capture), "ao-pcm-waveheader": "no"})
    for key, value in options.items():
        rc = mpv.mpv_set_option_string(ctx, key.encode(), value.encode())
        if rc < 0: raise RuntimeError((key, rc))
    if mpv.mpv_initialize(ctx) < 0: raise RuntimeError("mpv initialization failed")
    args = (c.c_char_p * 4)(b"loadfile", source.encode(), b"replace", None)
    started = time.perf_counter()
    samples = []
    restarts = []
    try:
        if mpv.mpv_command(ctx, args) < 0: raise RuntimeError("load failed")
        while time.perf_counter() - started < 20:
            event = mpv.mpv_wait_event(ctx, .005).contents
            if event.id == 21: restarts.append(round((time.perf_counter()-started)*1000, 2))
            pos = c.c_double()
            if mpv.mpv_get_property(ctx, b"time-pos", 5, c.byref(pos)) == 0:
                if not samples or pos.value != samples[-1][1]: samples.append((time.perf_counter()-started, pos.value))
            if event.id == 7: break
        else: raise RuntimeError("playback timed out")
    finally: mpv.mpv_terminate_destroy(ctx)
    gaps = [max(0, (b[0]-a[0])-(b[1]-a[1]))*1000 for a,b in zip(samples,samples[1:]) if 1.8<a[1]<2.2]
    return {"elapsed_seconds": round(time.perf_counter()-started,3), "restart_ms": restarts,
        "join_clock_stall_ms": round(max(gaps,default=0),3), "position_samples":len(samples)}

def seek_benchmark(source, next_position):
    ctx = mpv.mpv_create()
    for key,value in {"vo":"null","ao":"null","pause":"yes","terminal":"no","cache":"yes",
        "demuxer-max-bytes":"33554432","demuxer-max-back-bytes":"8388608","demuxer-readahead-secs":"2"}.items():
        if mpv.mpv_set_option_string(ctx,key.encode(),value.encode())<0: raise RuntimeError(key)
    if mpv.mpv_initialize(ctx)<0: raise RuntimeError("initialize")
    def command(*arguments):
        argv=(c.c_char_p*(len(arguments)+1))(*(a.encode() for a in arguments),None)
        if mpv.mpv_command(ctx,argv)<0: raise RuntimeError(arguments)
    def wait_restart():
        start=time.perf_counter()
        while time.perf_counter()-start<5:
            if mpv.mpv_wait_event(ctx,.005).contents.id==21: return (time.perf_counter()-start)*1000
        raise RuntimeError("seek restart timeout")
    class Memory(c.Structure):
        _fields_=[("cb",c.c_uint32),("faults",c.c_uint32)]+[(n,c.c_size_t) for n in
            ["peak_working","working","peak_paged","paged","peak_nonpaged","nonpaged","pagefile","peak_pagefile","private"]]
    def memory():
        counters=Memory(); counters.cb=c.sizeof(counters)
        get=c.windll.psapi.GetProcessMemoryInfo
        get.argtypes=[c.c_void_p,c.c_void_p,c.c_uint32]
        if not get(c.c_void_p(-1),c.byref(counters),c.sizeof(counters)): raise RuntimeError("memory counters")
        return counters.private
    samples=[]; memory_samples=[]
    try:
        command("loadfile",source,"replace"); wait_restart()
        for i in range(100):
            command("seek",str(.2 if i%2 else next_position),"absolute+exact")
            samples.append(wait_restart())
            if i in [19,39,59,79,99]: memory_samples.append(memory())
    finally: mpv.mpv_terminate_destroy(ctx)
    samples=sorted(samples[20:])
    return {"seek_median_ms":round(statistics.median(samples),3),"seek_p95_ms":round(samples[int(len(samples)*.95)],3),
        "process_private_mib_every_20_seeks":[round(v/1048576,2) for v in memory_samples]}

with probe_directory() as temp:
    directory=Path(temp)
    source=directory/"fixture,cut.mp4"
    subprocess.run([str(ROOT/"binaries/ffmpeg/win-x64/ffmpeg.exe"), "-v","error","-y",
        "-f","lavfi","-i","testsrc2=size=640x360:rate=60:duration=6",
        "-f","lavfi","-i", "aevalsrc=0.2*sin(2*PI*if(lt(t\\,2)\\,440\\,if(lt(t\\,4)\\,1000\\,660))*t):s=48000:d=6",
        "-vf","tinterlace=interleave_top", "-c:v","mpeg2video","-g","30","-flags","+ilme+ildct",
        "-c:a","ac3","-shortest",str(source)],check=True)
    before=hashlib.sha256(source.read_bytes()).hexdigest()
    name=str(source)
    edl="edl://"+";".join(f"%{len(name.encode())}%{name},{start},2" for start in [0,4])
    results={"normal":run(name),"collapsed_software":run(edl),"collapsed_auto":run(edl,hardware="auto-safe")}
    raw=directory/"joined.pcm"
    results["capture"]=run(edl,capture=raw)
    pcm=struct.unpack("<"+"h"*(raw.stat().st_size//2),raw.read_bytes())
    def frequency(offset):
        window=pcm[int(offset*48000):int((offset+.1)*48000)]
        return sum(a<=0<b for a,b in zip(window,window[1:]))*10
    results["normal_seek_stress"]=seek_benchmark(name,4.2)
    results["collapsed_seek_stress"]=seek_benchmark(edl,2.2)
    results["audio"]={"seconds":len(pcm)/48000,"before_hz":frequency(1.8),"after_hz":frequency(2.1)}
    assert abs(len(pcm)/48000-4)<.01, results
    assert abs(frequency(1.8)-440)<30 and abs(frequency(2.1)-660)<30, results
    assert hashlib.sha256(source.read_bytes()).hexdigest()==before
    print(json.dumps(results,indent=2))
