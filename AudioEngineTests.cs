using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
namespace RecordingLevelChecker {
internal static class FakeAudio {
    public static List<IntPtr> Headers=new List<IntPtr>();
    public static List<uint> Volumes=new List<uint>();
    public static int Opened,Closed,Pauses,Restarts; public static bool FailVolume;
    public static void Reset() { Headers.Clear(); Volumes.Clear(); Opened=Closed=Pauses=Restarts=0; FailVolume=false; }
    public static IntPtr Open(uint id) { Opened++; return (IntPtr)(100+id); }
    public static uint SetVolume(IntPtr h,uint volume) {
        if(h!=(IntPtr)101) throw new Exception("Volume touched the main output");
        Volumes.Add(volume); return FailVolume?8u:0u;
    }
    public static void Complete() {
        foreach(var pointer in Headers) { var header=(Native.Header)Marshal.PtrToStructure(pointer,typeof(Native.Header)); header.Flags|=1; Marshal.StructureToPtr(header,pointer,false); }
    }
}
public static class AudioEngineTests {
    static void Assert(bool ok,string message) { if(!ok) throw new Exception(message); Console.WriteLine("PASS: "+message); }
    public static void Main() {
        FakeAudio.Reset(); var volume=new MonitorVolume(); var pause=new MeasurementPause(); bool resumed=false;
        using(var timeout=new CancellationTokenSource(4000)) {
            var result=AudioEngine.Run(new byte[192000],999,0,timeout.Token,(a,t)=> {
                if(t>=0.1 && !resumed) { volume.Percent=0; pause.Requested=true; }
                if(pause.IsPaused) { pause.Requested=false; resumed=true; }
                if(t>=0.2) volume.Percent=75;
                if(t>=0.35) FakeAudio.Complete();
            },1,pause,false,volume);
            Assert(!result.Cancelled && result.Audio.Length==0 && !result.PossibleGap,"play-only completes without microphone access or capture results");
            Assert(result.PauseCount==1 && resumed && FakeAudio.Pauses>=4 && FakeAudio.Restarts>=4,"play-only pauses and resumes both outputs");
            Assert(FakeAudio.Volumes.SequenceEqual(new uint[]{uint.MaxValue,0,MonitorVolume.StereoValue(75)}),"live monitor volume changes and mute affect only the monitor handle");
            Assert(FakeAudio.Opened==2 && FakeAudio.Closed==2,"both output handles close after completion");
        }
        FakeAudio.Reset();
        using(var stop=new CancellationTokenSource()) {
            var result=AudioEngine.Run(new byte[192000],999,0,stop.Token,(a,t)=>stop.Cancel(),-1,null,false);
            Assert(result.Cancelled && FakeAudio.Opened==1 && FakeAudio.Closed==1 && FakeAudio.Volumes.Count==0,"stopping playback with no monitor releases output without setting volume");
        }
        FakeAudio.Reset(); FakeAudio.FailVolume=true; bool failed=false;
        try { AudioEngine.Run(new byte[192000],999,0,CancellationToken.None,(a,t)=>{},1,null,false); }
        catch(InvalidOperationException) { failed=true; }
        Assert(failed && FakeAudio.Headers.Count==0 && FakeAudio.Closed==2,"unsupported monitor volume fails before playback and releases both handles");
    }
}
}
