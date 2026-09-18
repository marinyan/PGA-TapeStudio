using System;
using System.IO;
using System.Threading;
using RecordingLevelChecker;
public static class Tests {
    static void Check(bool value,string name) { if(!value) throw new Exception(name); Console.WriteLine("PASS: "+name); }
    static short[] Signal(int count) {
        var random=new Random(721); var data=new short[count]; double smooth=0;
        for(int i=0;i<count;i++) { smooth=0.7*smooth+0.3*(random.NextDouble()*2-1); data[i]=(short)(smooth*20000); }
        return data;
    }
    static byte[] Stereo(short[] signal) {
        byte[] pcm=new byte[signal.Length*4]; for(int i=0;i<signal.Length;i++) { pcm[i*4]=(byte)signal[i]; pcm[i*4+1]=(byte)(signal[i]>>8); pcm[i*4+2]=pcm[i*4]; pcm[i*4+3]=pcm[i*4+1]; } return pcm;
    }
    static short[] Capture(short[] source,int lag,double gain) {
        var recording=new short[source.Length+lag+24000]; for(int i=0;i<source.Length;i++) recording[i+lag]=(short)(source[i]*gain); return recording;
    }
    public static void Main() {
        var low=Stereo(new short[]{0,1000,-2000,4000});
        // Make the right channel louder than the left to test linked stereo normalization.
        low[14]=0x40; low[15]=0x1f; // 8000
        var normalized=DigitalGain.Normalize(low,CancellationToken.None);
        int ceiling=(int)Math.Floor(32768*Math.Pow(10,-1.0/20));
        int actualPeak=0; for(int i=0;i<low.Length;i+=2) actualPeak=Math.Max(actualPeak,Math.Abs((int)BitConverter.ToInt16(low,i)));
        Check(normalized.GainDb>0 && actualPeak<=ceiling && actualPeak>ceiling*0.985,"automatic gain raises the loudest channel to just below -1 dBFS");
        Check(Math.Abs(BitConverter.ToInt16(low,12)*2-BitConverter.ToInt16(low,14))<=1 && BitConverter.ToInt16(low,0)==0,"automatic gain preserves stereo balance and silence");
        var hot=DigitalGain.Normalize(Stereo(new short[]{short.MinValue,short.MaxValue}),CancellationToken.None);
        Check(hot.GainDb<0 && BitConverter.ToInt16(hot.Pcm,0)>short.MinValue && Math.Abs((int)BitConverter.ToInt16(hot.Pcm,0))<=ceiling,"full scale including -32768 is attenuated without overflow");
        var silentGain=DigitalGain.Normalize(new byte[400],CancellationToken.None);
        Check(silentGain.Silent && silentGain.GainDb==0,"silent source is not amplified");
        var tiny=DigitalGain.Normalize(Stereo(new short[]{1,-1}),CancellationToken.None);
        Check(tiny.GainDb<96 && BitConverter.ToInt16(tiny.Pcm,0)>28000,"one-LSB input has finite gain without overflow");
        var gainCancel=new CancellationTokenSource(); gainCancel.Cancel(); bool gainStopped=false;
        try { DigitalGain.Normalize(new byte[400],gainCancel.Token); } catch(OperationCanceledException) { gainStopped=true; }
        Check(gainStopped,"automatic gain analysis respects cancellation");
        var decodeOk=new RecognitionResult { Files=1, BitErrors=0, ByteErrors=0, Bytes=new byte[]{1,2,3} };
        Check(TapeRecognition.CompareBytes("test",decodeOk,decodeOk).Contains("完全一致"),"recognized identical byte streams pass equality");
        Check(TapeRecognition.CompareBytes("test",decodeOk,new RecognitionResult()).Contains("復調データなし"),"empty decoder results do not pass equality");
        Check(TapeRecognition.CompareBytes("test",decodeOk,new RecognitionResult { Bytes=new byte[]{1,3,3} }).Contains("0x1"),"decoded mismatch reports first byte offset");
        Check(TapeRecognition.CompareBytes("test",decodeOk,new RecognitionResult { Bytes=new byte[]{1,2} }).Contains("不一致"),"truncated decoder stream is not equal");
        Check(TapeRecognition.CompareBytes("test",decodeOk,new RecognitionResult { Error="failed" }).Contains("判定不能"),"decoder failures stay inconclusive");
        var pause=new MeasurementPause(); int pauses=0,resumes=0;
        Action onPause=delegate { pauses++; }, onResume=delegate { resumes++; };
        pause.Apply(onPause,onResume); pause.Requested=true; pause.Apply(onPause,onResume); pause.Apply(onPause,onResume);
        Check(pause.IsPaused && pause.Count==1 && pauses==1 && resumes==0,"pause transition is applied once");
        pause.Requested=false; pause.Apply(onPause,onResume); pause.Apply(onPause,onResume);
        Check(!pause.IsPaused && pause.Count==1 && resumes==1,"resume transition is applied once");
        pause.Requested=true; pause.Apply(onPause,onResume); Check(pause.Count==2,"repeated pause is counted");
        var failedPause=new MeasurementPause { Requested=true }; bool pauseFailed=false;
        try { failedPause.Apply(delegate { throw new IOException("test"); },onResume); } catch(IOException) { pauseFailed=true; }
        Check(pauseFailed && !failedPause.IsPaused && failedPause.Count==0,"failed pause does not report success");
        AudioEngine.ValidateOutputs(0,-1); AudioEngine.ValidateOutputs(0,1); bool duplicate=false;
        try { AudioEngine.ValidateOutputs(0,0); } catch(InvalidOperationException) { duplicate=true; }
        Check(duplicate,"monitor allows disabled or distinct output and rejects duplicates");
        string config=Path.Combine(Path.GetTempPath(),"level-settings-test-"+Guid.NewGuid()+".xml");
        try {
            var prefs=new AppSettings { InputName="MIC 日本語", OutputName="OUT", WholeFile=false, Seconds=1234, Speed=3, Gain=-22, LastFile="C:\\audio test\\音声.wav", Zoom=100, WaveChannel=2, AlignWaves=false,AutoGain=true };
            prefs.Save(config); var loaded=AppSettings.Load(config);
            Check(loaded.AutoGain,"automatic gain setting round trip");
            Check(loaded.InputName==prefs.InputName && loaded.OutputName=="OUT" && loaded.LastFile==prefs.LastFile && loaded.Speed==3 && loaded.Gain==-22 && loaded.Seconds==1234 && !loaded.WholeFile && loaded.Zoom==100 && loaded.WaveChannel==2 && !loaded.AlignWaves,"settings round trip");
            prefs.WholeFile=true; prefs.Speed=999; prefs.MonitorEnabled=true; prefs.MonitorName="Headphones 日本語"; prefs.Save(config); loaded=AppSettings.Load(config);
            Check(loaded.WholeFile && loaded.Speed==4,"settings atomic overwrite and range validation");
            Check(loaded.MonitorEnabled && loaded.MonitorName==prefs.MonitorName,"monitor settings round trip");
            File.WriteAllText(config,"<AppSettings><OutputName>OUT</OutputName></AppSettings>");
            Check(!AppSettings.Load(config).MonitorEnabled,"older settings default to no monitor");
            File.WriteAllText(config,"<AppSettings><KeepPitch>true</KeepPitch><Speed>2</Speed></AppSettings>");
            var migrated=AppSettings.Load(config); migrated.Save(config);
            Check(!File.ReadAllText(config).Contains("KeepPitch"),"obsolete pitch preservation setting is discarded");
            File.WriteAllText(config,"速度: 2 倍\n出力ゲイン: -18 dB\n音程維持: True\n測定上限: 全部");
            double oldSpeed=1,oldGain=0; bool oldPitch=false; int oldSeconds=15;
            TapeRecognition.ReadRecordedSettings(config,ref oldSpeed,ref oldGain,ref oldPitch,ref oldSeconds);
            Check(oldSpeed==2 && oldGain==-18 && oldPitch && oldSeconds==0,"historical recording conditions are read only for offline recognition");
            File.WriteAllText(config,"broken"); bool rejected=false;
            try { AppSettings.Load(config); } catch(InvalidOperationException) { rejected=true; }
            Check(rejected,"corrupt settings detected");
            Check(AppSettings.FindDevice(new string[]{"B","A"},"A")==1 && AppSettings.FindDevice(new string[]{"B"},"A")==-1 && AppSettings.FindDevice(new string[]{"A","A"},"A")==-1,"device restore handles reordered missing and ambiguous names");
        } finally { File.Delete(config); }
        byte[] channels={0,128,255,127,255,127,255,127};
        Check(WaveView.ExtractPlayback(channels,0)[0]==-32768 && WaveView.ExtractPlayback(channels,1)[0]==32767 && WaveView.ExtractPlayback(channels,2)[1]==32767,"waveform channel decoding without overflow");
        Check(WaveView.PlaybackIndex(15337,15337,true)==0 && WaveView.PlaybackIndex(100,15337,true)<0 && WaveView.PlaybackIndex(100,15337,false)==100,"waveform alignment maps recording time to output time");
        var quiet=new Analysis(); quiet.Add(new short[48000]); Check(quiet.PeakDb==-120 && quiet.FlatRuns==0,"silence");
        var rails=new Analysis(); rails.Add(new short[]{32767,32767,32767,32767,32767,-32768}); Check(rails.NearFullScale==6 && rails.FlatRuns==1,"rail and plateau detection");
        var split=new Analysis(); split.Add(new short[]{18000,18000}); split.Add(new short[]{18000,18000,18000}); Check(split.FlatRuns==1,"plateau across audio buffers");
        short[] source=Signal(144000); byte[] pcm=Stereo(source); short[] recorded=Capture(source,15337,-0.4);
        var clean=SignalComparison.Compare(pcm,recorded,CancellationToken.None);
        Check(clean.Aligned && clean.SuspiciousWindows==0 && Math.Abs(clean.DelayMs-15337/48.0)<0.03,"delay, gain and polarity compensation");
        Check(clean.MeanSimilarity>0.999 && clean.MinimumSimilarity>0.999 && clean.LowSimilarityWindows==0,"scaled inverted waveforms have high similarity");
        var dc=Capture(source,15337,0.4);
        for(int i=15337;i<15337+source.Length;i++) dc[i]+=3000;
        var dcResult=SignalComparison.Compare(pcm,dc,CancellationToken.None);
        Check(dcResult.MeanSimilarity>0.999 && dcResult.SuspiciousWindows==0,"DC offset does not change waveform similarity");
        var sparse=Capture(source,15337,0.4);
        for(int i=48000;i<72000;i++) if(i%8==1) sparse[i+15337]=(short)(i%16==1?16000:-16000);
        var sparseResult=SignalComparison.Compare(pcm,sparse,CancellationToken.None);
        Check(sparseResult.Aligned && sparseResult.LowSimilarityWindows>0 && sparseResult.MinimumSimilarity<0.95,"full-sample similarity catches changes between decimated samples");
        var flipped=Capture(source,15337,0.4);
        for(int i=48000;i<72000;i++) flipped[i+15337]=(short)-flipped[i+15337];
        var flippedResult=SignalComparison.Compare(pcm,flipped,CancellationToken.None);
        Check(flippedResult.Aligned && flippedResult.MinimumSimilarity<0.01 && flippedResult.LowSimilarityWindows>0,"local polarity reversal is not hidden by absolute correlation");
        var tailSource=Signal(146400); var tail=Capture(tailSource,15337,0.4);
        Array.Clear(tail,15337+144000,2400);
        var tailResult=SignalComparison.Compare(Stereo(tailSource),tail,CancellationToken.None);
        Check(tailResult.SimilarityWindows.Count==7 && tailResult.LowSimilarityWindows>0,"short final window is checked for lost audio");
        Array.Clear(recorded,15337+48000,24000);
        var dropout=SignalComparison.Compare(pcm,recorded,CancellationToken.None); Check(dropout.Aligned && dropout.SuspiciousWindows>0,"missing audio detection");
        recorded=Capture(source,15337,0.4); recorded[15337+48111]=32767;
        var click=SignalComparison.Compare(pcm,recorded,CancellationToken.None); Check(click.Aligned && click.SuspiciousWindows>0,"single-sample click detection");
        recorded=Capture(source,15337,1); for(int i=0;i<recorded.Length;i++) recorded[i]=(short)Math.Max(-1800,Math.Min(1800,(int)recorded[i]));
        var clipped=SignalComparison.Compare(pcm,recorded,CancellationToken.None); Check(clipped.Aligned && clipped.SuspiciousWindows>0,"clipped waveform comparison");
        var noSignal=SignalComparison.Compare(pcm,new short[recorded.Length],CancellationToken.None); Check(!noSignal.Aligned,"absent recording is inconclusive");
        var cancelled=new CancellationTokenSource(); cancelled.Cancel(); bool stopped=false;
        try { SignalComparison.Compare(pcm,recorded,cancelled.Token); } catch(OperationCanceledException) { stopped=true; }
        Check(stopped,"comparison cancellation");
        var unrelated=SignalComparison.Compare(pcm,Signal(recorded.Length/2),CancellationToken.None); Check(unrelated.Coverage<0.98 || !unrelated.Aligned,"truncated recording cannot pass");
        string temp=Path.Combine(Path.GetTempPath(),"level-check-test-"+Guid.NewGuid()+".wav");
        try {
            AudioEngine.SaveWave(temp,source); Check(new FileInfo(temp).Length==44+source.Length*2,"WAV header and length");
            byte[] normal=Decoder.DecodeForPlayback(temp,0,0,CancellationToken.None);
            var normalMono=WaveView.ExtractPlayback(normal,0); bool sameTiming=normalMono.Length==source.Length;
            // FFmpeg applies -3 dB when mapping mono to stereo; timing must stay sample-for-sample.
            for(int i=0;sameTiming && i<source.Length;i++) sameTiming=Math.Abs(normalMono[i]-source[i]/Math.Sqrt(2))<1.1;
            Check(sameTiming,"play-only preserves sample timing at 100 percent speed");
            Check(AppSettings.DroppedFile(new string[]{temp})==temp && AppSettings.DroppedFile(new string[]{temp,temp})==null && AppSettings.DroppedFile(new string[]{Path.GetTempPath()})==null && AppSettings.DroppedFile(null)==null,"file drop accepts one file and rejects directories or multiple files");
            byte[] sped=Decoder.Decode(temp,2,-18,15,true,CancellationToken.None);
            Check(Math.Abs(sped.Length/192000.0-1.5)<0.15,"FFmpeg pitch-preserving 2x playback duration");
            var fast=Decoder.Decode(temp,4,-18,1,false,CancellationToken.None); Check(Math.Abs(fast.Length/192000.0-0.75)<0.05,"4x rate conversion");
            var longSource=new short[48000*131]; longSource[longSource.Length-1]=16000;
            AudioEngine.SaveWave(temp,longSource);
            var whole=Decoder.Decode(temp,1,0,0,false,CancellationToken.None);
            Check(whole.Length==longSource.Length*4 && BitConverter.ToInt16(whole,whole.Length-4)>1000,"whole file includes final sample beyond 120 seconds");
            var limited=Decoder.Decode(temp,1,0,1,false,CancellationToken.None);
            Check(limited.Length==192000,"limited duration still stops at requested time");
            var gainSource=new short[96000]; for(int i=0;i<48000;i++) gainSource[i]=(short)(1000*Math.Sin(i*0.13)); gainSource[90000]=16000;
            AudioEngine.SaveWave(temp,gainSource);
            var autoWhole=Decoder.PrepareForPlayback(temp,-18,0,true,CancellationToken.None);
            var autoPart=Decoder.PrepareForPlayback(temp,-40,1,true,CancellationToken.None);
            Check(autoWhole.Pcm.Length==384000 && autoPart.Pcm.Length==192000 && autoWhole.GainDb==autoPart.GainDb,"automatic gain scans the complete WAV and keeps playback at 100 percent");
            Check(System.Linq.Enumerable.SequenceEqual(System.Linq.Enumerable.Take(autoWhole.Pcm,192000),autoPart.Pcm),"partial automatic playback uses full-file peak and ignores manual gain");
            var autoFast=Decoder.Prepare(temp,2,-18,0,true,false,CancellationToken.None);
            int fastPeak=0; for(int i=0;i<autoFast.Pcm.Length;i+=2) fastPeak=Math.Max(fastPeak,Math.Abs((int)BitConverter.ToInt16(autoFast.Pcm,i)));
            Check(autoFast.Pcm.Length==192000 && fastPeak<=ceiling && fastPeak>ceiling*0.985,"measurement automatic gain is measured after speed conversion");
        } finally { File.Delete(temp); }
    }
}
