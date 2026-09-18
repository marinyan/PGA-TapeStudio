using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RecordingLevelChecker {
public static class Program {
    [STAThread] public static void Main(string[] args) {
        if(args.Length==3 && args[0]=="--recognize") { Environment.ExitCode=TapeRecognition.Worker(args[1],args[2]); return; }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

public sealed class Analysis {
    public long Samples, NearFullScale, RailSamples, FlatRuns;
    public double Peak, SumSquares;
    public int PeakIndex;
    int flatLength; short previous; bool hasPrevious;
    public double PeakDb { get { return Db(Peak); } }
    public double RmsDb { get { return Db(Samples == 0 ? 0 : Math.Sqrt(SumSquares / Samples)); } }
    public static double Db(double amplitude) { return amplitude <= 0 ? -120 : Math.Max(-120, 20 * Math.Log10(amplitude)); }
    public void Add(short[] data) {
        foreach (short sample in data) {
            double a = Math.Abs((double)sample) / 32768;
            if (a > Peak) { Peak = a; PeakIndex = (int)Samples; }
            SumSquares += a * a;
            if (a >= Math.Pow(10, -0.1 / 20)) NearFullScale++;
            if (Math.Abs((int)sample) >= 32760) RailSamples++;
            // A heuristic only: low-frequency tones and compressed sources can also have plateaus.
            if (hasPrevious && Math.Abs(sample - (int)previous) <= 2 && a >= 0.1 && Math.Sign(sample) == Math.Sign(previous)) flatLength++;
            else flatLength = 1;
            if (flatLength == 5) FlatRuns++;
            previous = sample; hasPrevious = true; Samples++;
        }
    }
    public string Verdict {
        get {
            if (Samples == 0) return "未測定";
            if (PeakDb < -60) return "信号が小さすぎます — 接続・入力先を確認";
            if (NearFullScale > 0) return "上限付近の入力あり — ゲインを下げて再測定";
            if (FlatRuns > 0) return "平坦な波形あり — ピークカットの疑い（参考判定）";
            return "検出条件に該当なし — 歪みがないことの保証ではありません";
        }
    }
}

internal static class Native {
    [StructLayout(LayoutKind.Sequential, Pack=2)] public struct Format {
        public ushort Tag, Channels; public uint Rate, BytesPerSec; public ushort Align, Bits, Extra;
        public static Format Pcm(ushort channels) { return new Format { Tag=1, Channels=channels, Rate=48000, BytesPerSec=(uint)(96000*channels), Align=(ushort)(2*channels), Bits=16 }; }
    }
    [StructLayout(LayoutKind.Sequential)] public struct Header {
        public IntPtr Data; public uint Length, Recorded; public UIntPtr User; public uint Flags, Loops; public IntPtr Next; public UIntPtr Reserved;
    }
    [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet=CharSet.Unicode)] public static extern uint waveInGetDevCapsW(UIntPtr id, IntPtr caps, uint size);
    [DllImport("winmm.dll", CharSet=CharSet.Unicode)] public static extern uint waveOutGetDevCapsW(UIntPtr id, IntPtr caps, uint size);
    [DllImport("winmm.dll")] public static extern uint waveInOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] public static extern uint waveOutOpen(out IntPtr handle, uint id, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] public static extern uint waveInPrepareHeader(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveOutPrepareHeader(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveInUnprepareHeader(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveOutUnprepareHeader(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveInAddBuffer(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveOutWrite(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] public static extern uint waveInStart(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint waveInStop(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint waveOutPause(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint waveOutRestart(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint waveOutSetVolume(IntPtr h, uint volume);
    [DllImport("winmm.dll")] public static extern uint waveInReset(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint waveInClose(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint waveOutClose(IntPtr h);
    public static void Check(uint result, string action) { if (result != 0) throw new InvalidOperationException(action + "に失敗しました (Windows audio: " + result + ")。デバイスの接続・使用中アプリ・マイク権限を確認してください。"); }
    public static string DeviceName(uint id, bool input) {
        IntPtr caps = Marshal.AllocHGlobal(84);
        try { Check(input ? waveInGetDevCapsW((UIntPtr)id, caps, 80) : waveOutGetDevCapsW((UIntPtr)id, caps, 84), "デバイス取得"); return Marshal.PtrToStringUni(IntPtr.Add(caps, 8), 32).TrimEnd('\0'); }
        finally { Marshal.FreeHGlobal(caps); }
    }
}
internal sealed class AudioBuffer : IDisposable {
    public IntPtr Pointer, Data; public bool Prepared;
    public static readonly uint Size = (uint)Marshal.SizeOf(typeof(Native.Header));
    public Native.Header Header { get { return (Native.Header)Marshal.PtrToStructure(Pointer, typeof(Native.Header)); } }
    public AudioBuffer(int bytes) {
        Data = Marshal.AllocHGlobal(bytes); Pointer = Marshal.AllocHGlobal((int)Size);
        Marshal.StructureToPtr(new Native.Header { Data=Data, Length=(uint)bytes }, Pointer, false);
    }
    public void Dispose() { Marshal.FreeHGlobal(Pointer); Marshal.FreeHGlobal(Data); }
}

public sealed class CaptureResult {
    public short[] Audio; public Analysis Stats; public bool Cancelled; public bool PossibleGap; public int PauseCount;
}
public sealed class MeasurementPause {
    public volatile bool Requested, IsPaused;
    public int Count { get; private set; }
    public void Apply(Action pause,Action resume) {
        bool requested=Requested;
        if(requested==IsPaused) return;
        if(requested) { pause(); Count++; } else resume();
        IsPaused=requested;
    }
}
public sealed class MonitorVolume {
    public volatile int Percent=100;
    public static uint StereoValue(int percent) {
        uint level=(uint)(Math.Max(0,Math.Min(100,percent))*65535/100);
        return level | (level<<16);
    }
}
public static class AudioEngine {
    public static void ValidateOutputs(uint output,int monitor) {
        if(monitor>=0 && output==(uint)monitor) throw new InvalidOperationException("測定用とモニター用には別の出力先を選択してください。");
    }
    public static CaptureResult Run(byte[] playback, uint input, uint output, CancellationToken token, Action<Analysis,double> progress, int monitor=-1, MeasurementPause pause=null, bool record=true, MonitorVolume monitorVolume=null) {
        ValidateOutputs(output,monitor);
        if(pause==null) pause=new MeasurementPause();
        if(monitorVolume==null) monitorVolume=new MonitorVolume();
        int appliedVolume=-1;
        IntPtr hi=IntPtr.Zero, ho=IntPtr.Zero, hm=IntPtr.Zero;
        var buffers = new List<AudioBuffer>(); AudioBuffer play=null, monitorPlay=null;
        var samples = new List<short>(); var stats = new Analysis();
        var watch = new Stopwatch(); bool gap=false; int next=0;
        try {
            var fi=Native.Format.Pcm(1); var fo=Native.Format.Pcm(2);
            if(record) Native.Check(Native.waveInOpen(out hi, input, ref fi, IntPtr.Zero, IntPtr.Zero, 0), "MIC INを開く処理");
            Native.Check(Native.waveOutOpen(out ho, output, ref fo, IntPtr.Zero, IntPtr.Zero, 0), "LINE OUTを開く処理");
            if(monitor>=0) Native.Check(Native.waveOutOpen(out hm,(uint)monitor,ref fo,IntPtr.Zero,IntPtr.Zero,0),"モニター出力を開く処理");
            if(hm!=IntPtr.Zero) {
                appliedVolume=monitorVolume.Percent;
                Native.Check(Native.waveOutSetVolume(hm,MonitorVolume.StereoValue(appliedVolume)),"モニター音量の設定（非対応デバイスの場合はモニターを「なし」にしてください）");
            }
            for (int i=0;record && i<16;i++) {
                var b=new AudioBuffer(4800); buffers.Add(b);
                Native.Check(Native.waveInPrepareHeader(hi,b.Pointer,AudioBuffer.Size),"録音準備"); b.Prepared=true;
                Native.Check(Native.waveInAddBuffer(hi,b.Pointer,AudioBuffer.Size),"録音バッファ");
            }
            play=new AudioBuffer(playback.Length); Marshal.Copy(playback,0,play.Data,playback.Length);
            Native.Check(Native.waveOutPrepareHeader(ho,play.Pointer,AudioBuffer.Size),"再生準備"); play.Prepared=true;
            if(hm!=IntPtr.Zero) {
                monitorPlay=new AudioBuffer(playback.Length); Marshal.Copy(playback,0,monitorPlay.Data,playback.Length);
                Native.Check(Native.waveOutPrepareHeader(hm,monitorPlay.Pointer,AudioBuffer.Size),"モニター再生準備"); monitorPlay.Prepared=true;
            }
            if(record) Native.Check(Native.waveInStart(hi),"録音開始"); watch.Start();
            bool playing=false; double ended=-1, lastProgress=0;
            while (!token.IsCancellationRequested) {
                int requestedVolume=monitorVolume.Percent;
                if(hm!=IntPtr.Zero && appliedVolume!=requestedVolume) {
                    Native.Check(Native.waveOutSetVolume(hm,MonitorVolume.StereoValue(requestedVolume)),"モニター音量の変更");
                    appliedVolume=requestedVolume;
                }
                bool wasPaused=pause.IsPaused;
                pause.Apply(delegate {
                    Native.Check(Native.waveOutPause(ho),"再生の一時停止");
                    if(hm!=IntPtr.Zero) Native.Check(Native.waveOutPause(hm),"モニターの一時停止");
                    if(record) Native.Check(Native.waveInStop(hi),"録音の一時停止"); watch.Stop();
                },delegate {
                    if(record) Native.Check(Native.waveInStart(hi),"録音の再開");
                    Native.Check(Native.waveOutRestart(ho),"再生の再開");
                    if(hm!=IntPtr.Zero) Native.Check(Native.waveOutRestart(hm),"モニターの再開");
                    watch.Start();
                });
                if(wasPaused!=pause.IsPaused) progress(stats,watch.Elapsed.TotalSeconds);
                double elapsed=watch.Elapsed.TotalSeconds;
                if (!pause.IsPaused && !playing && elapsed>=(record?0.3:0)) {
                    Native.Check(Native.waveOutPause(ho),"再生開始の準備");
                    if(hm!=IntPtr.Zero) Native.Check(Native.waveOutPause(hm),"モニター開始の準備");
                    Native.Check(Native.waveOutWrite(ho,play.Pointer,AudioBuffer.Size),"再生開始");
                    if(hm!=IntPtr.Zero) Native.Check(Native.waveOutWrite(hm,monitorPlay.Pointer,AudioBuffer.Size),"モニター再生開始");
                    Native.Check(Native.waveOutRestart(ho),"再生開始");
                    if(hm!=IntPtr.Zero) Native.Check(Native.waveOutRestart(hm),"モニター再生開始");
                    playing=true;
                }
                if (playing && (play.Header.Flags & 1)!=0 && ended<0) ended=elapsed;
                int ready=0; foreach (var b in buffers) if ((b.Header.Flags & 1)!=0) ready++;
                if (record && ready==buffers.Count) gap=true;
                for (int n=0;n<buffers.Count;n++) {
                    var b=buffers[next]; var header=b.Header;
                    if ((header.Flags & 1)==0) break;
                    AddCaptured(b, samples, stats);
                    Native.Check(Native.waveInAddBuffer(hi,b.Pointer,AudioBuffer.Size),"録音継続"); next=(next+1)%buffers.Count;
                }
                if (elapsed-lastProgress>=0.1) { progress(stats,elapsed); lastProgress=elapsed; }
                if (!pause.IsPaused && ended>=0 && elapsed-ended>=(record?0.5:0)) break;
                if (elapsed>playback.Length/192000.0+10) throw new IOException("再生デバイスから完了通知がありません。測定を中止しました。");
                Thread.Sleep(5);
            }
            Native.Check(Native.waveOutReset(ho),"再生停止");
            if(record) Native.Check(Native.waveInReset(hi),"録音停止");
            for (int n=0;n<buffers.Count;n++) { var b=buffers[(next+n)%buffers.Count]; if ((b.Header.Flags & 1)!=0) AddCaptured(b,samples,stats); }
            return new CaptureResult { Audio=samples.ToArray(), Stats=stats, Cancelled=token.IsCancellationRequested, PossibleGap=gap, PauseCount=pause.Count };
        } finally {
            if(hm!=IntPtr.Zero) Native.waveOutReset(hm);
            if (ho!=IntPtr.Zero) Native.waveOutReset(ho);
            if (hi!=IntPtr.Zero) Native.waveInReset(hi);
            foreach (var b in buffers) { if (!b.Prepared || Native.waveInUnprepareHeader(hi,b.Pointer,AudioBuffer.Size)==0) b.Dispose(); }
            if (play!=null && (!play.Prepared || Native.waveOutUnprepareHeader(ho,play.Pointer,AudioBuffer.Size)==0)) play.Dispose();
            if(monitorPlay!=null && (!monitorPlay.Prepared || Native.waveOutUnprepareHeader(hm,monitorPlay.Pointer,AudioBuffer.Size)==0)) monitorPlay.Dispose();
            if (hi!=IntPtr.Zero) Native.waveInClose(hi);
            if (ho!=IntPtr.Zero) Native.waveOutClose(ho);
            if(hm!=IntPtr.Zero) Native.waveOutClose(hm);
        }
    }
    static void AddCaptured(AudioBuffer buffer,List<short> samples,Analysis stats) {
        int count=(int)buffer.Header.Recorded/2; if (count==0) return;
        var data=new short[count]; Marshal.Copy(buffer.Data,data,0,count); samples.AddRange(data); stats.Add(data);
    }
    public static void SaveWave(string path,short[] samples) {
        using (var w=new BinaryWriter(File.Create(path))) {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36+samples.Length*2); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples.Length*2); foreach (short s in samples) w.Write(s);
        }
    }
}

public sealed class PreparedAudio {
    public byte[] Pcm;
    public double GainDb, SourcePeakDb;
    public bool AutoGain, Silent;
    public string GainDescription {
        get { return !AutoGain?"手動ゲイン":Silent?"自動ゲイン：無音のため0 dB（増幅なし）":string.Format("自動ゲイン：元ピーク {0:F2} dBFS → 目標 −1 dBFS / 適用 {1:+0.0;-0.0;0.0} dB",SourcePeakDb,GainDb); }
    }
}
public static class DigitalGain {
    // A single linked gain for both channels, measured over the entire converted file.
    public static PreparedAudio Normalize(byte[] pcm,CancellationToken token) {
        if(pcm==null || pcm.Length==0 || pcm.Length%4!=0) throw new ArgumentException("ステレオPCMが不正です。");
        int peak=0;
        for(int i=0;i<pcm.Length;i+=2) {
            if((i&65535)==0) token.ThrowIfCancellationRequested();
            peak=Math.Max(peak,Math.Abs((int)(short)(pcm[i]|pcm[i+1]<<8)));
        }
        var result=new PreparedAudio { Pcm=pcm,AutoGain=true,Silent=peak==0,SourcePeakDb=Analysis.Db(peak/32768.0) };
        if(peak==0) return result;
        // Round down to 0.1 dB so rounding never pushes the peak above the ceiling.
        int ceiling=(int)Math.Floor(32768*Math.Pow(10,-1.0/20));
        result.GainDb=Math.Floor(20*Math.Log10((double)ceiling/peak)*10)/10;
        double factor=Math.Pow(10,result.GainDb/20);
        for(int i=0;i<pcm.Length;i+=2) {
            if((i&65535)==0) token.ThrowIfCancellationRequested();
            short value=(short)Math.Round((short)(pcm[i]|pcm[i+1]<<8)*factor);
            pcm[i]=(byte)value; pcm[i+1]=(byte)(value>>8);
        }
        return result;
    }
}
public static class Decoder {
    public static PreparedAudio PrepareForPlayback(string path,double gain,int seconds,bool autoGain,CancellationToken token) {
        return Prepare(path,1,gain,seconds,autoGain,false,token);
    }
    public static PreparedAudio Prepare(string path,double speed,double gain,int seconds,bool autoGain,bool keepPitch,CancellationToken token) {
        if(!autoGain) return new PreparedAudio { Pcm=Decode(path,speed,gain,seconds,keepPitch,token),GainDb=gain };
        if(seconds<0) throw new ArgumentOutOfRangeException("seconds");
        var result=DigitalGain.Normalize(Decode(path,speed,0,0,keepPitch,token),token);
        // Even for a partial measurement, base the gain on the peak of the full file.
        if(seconds>0 && (long)seconds*192000<result.Pcm.Length) Array.Resize(ref result.Pcm,seconds*192000);
        return result;
    }
    public static byte[] DecodeForPlayback(string path,double gain,int seconds,CancellationToken token) {
        return Decode(path,1,gain,seconds,false,token);
    }
    public static string FindFfmpeg() {
        string local=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ffmpeg.exe"); if(File.Exists(local)) return local;
        foreach(string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
            try { string p=Path.Combine(dir.Trim('"'),"ffmpeg.exe"); if(File.Exists(p)) return p; } catch(ArgumentException) { }
        }
        throw new FileNotFoundException("FFmpegが見つかりません。ffmpeg.exeをbinフォルダーへ置くか、PATHに追加してください。");
    }
    public static byte[] Decode(string path,double speed,double gain,int seconds,bool keepPitch,CancellationToken token) {
        if(seconds<0) throw new ArgumentOutOfRangeException("seconds");
        string filter=keepPitch ? "atempo="+speed.ToString(CultureInfo.InvariantCulture) : "asetrate=48000*"+speed.ToString(CultureInfo.InvariantCulture)+",aresample=48000";
        string temp=Path.Combine(Path.GetTempPath(),"level-check-"+Guid.NewGuid().ToString("N")+".pcm");
        try {
            var info=new ProcessStartInfo(FindFfmpeg(),"-nostdin -hide_banner -loglevel error -y -i \""+path+"\" -vn -map 0:a:0 -ar 48000 -ac 2 -af \"aresample=48000,"+filter+",volume="+gain.ToString(CultureInfo.InvariantCulture)+"dB\""+(seconds==0?"":" -t "+seconds)+" -f s16le \""+temp+"\"") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardError=true };
            using(var p=Process.Start(info)) {
                Task<string> error=p.StandardError.ReadToEndAsync();
                while(!p.WaitForExit(100)) {
                    bool tooLarge=File.Exists(temp) && new FileInfo(temp).Length>int.MaxValue-1048576L;
                    if(token.IsCancellationRequested || tooLarge) { p.Kill(); p.WaitForExit(); token.ThrowIfCancellationRequested(); throw new IOException("変換後の音声が大きすぎます（約2 GB以上）。測定秒数を指定するか、音源を分割してください。"); }
                }
                token.ThrowIfCancellationRequested();
                if(p.ExitCode!=0) throw new IOException("音声を読み込めません。\n"+error.Result);
            }
            if(new FileInfo(temp).Length>int.MaxValue-1048576L) throw new IOException("変換後の音声が大きすぎます（約2 GB以上）。音源を分割してください。");
            token.ThrowIfCancellationRequested();
            byte[] pcm=File.ReadAllBytes(temp); if(pcm.Length<4) throw new IOException("音声データがありません。"); return pcm;
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed class WaveView : Control {
    public short[] Samples, Playback; public int Center; public int WindowMs=50;
    public int DelaySamples; public bool Aligned; public string OutputChannel="左";
    public static short[] ExtractPlayback(byte[] pcm,int channel) {
        if(pcm==null) return null;
        var data=new short[pcm.Length/4];
        for(int i=0;i<data.Length;i++) {
            short left=(short)(pcm[4*i]|pcm[4*i+1]<<8), right=(short)(pcm[4*i+2]|pcm[4*i+3]<<8);
            data[i]=channel==0?left:channel==1?right:(short)(((int)left+right)/2);
        }
        return data;
    }
    public static int PlaybackIndex(int recordingIndex,int delaySamples,bool aligned) { return recordingIndex-(aligned?delaySamples:0); }
    public WaveView() { DoubleBuffered=true; BackColor=Color.FromArgb(17,25,39); ForeColor=Color.LightGray; }
    protected override void OnPaint(PaintEventArgs e) {
        base.OnPaint(e); if(Width<2 || Height<20) return;
        int total=Math.Max(Samples==null?0:Samples.Length,(Playback==null?0:Playback.Length)+(Aligned?DelaySamples:0));
        int length=Math.Min(Math.Max(1,total),48*WindowMs);
        int start=Math.Max(0,Math.Min(total-length,Center-length/2));
        var g=e.Graphics;
        string timing=Aligned?string.Format("遅延補正 {0:F2} ms / 横軸は録音時刻",DelaySamples/48.0):"未整列：各データの先頭を0秒として表示";
        g.DrawString(timing+"  /  共通振幅 ±1.0 = 0 dBFS",Font,Brushes.LightGray,10,5);
        int laneHeight=Math.Max(1,(Height-30)/2);
        DrawLane(g,Playback,PlaybackIndex(start,DelaySamples,Aligned),length,30,laneHeight,"出力（再生用PCM / "+OutputChannel+"）",Color.FromArgb(255,190,85));
        DrawLane(g,Samples,start,length,30+laneHeight,laneHeight,"入力（MIC IN録音）",Color.FromArgb(62,214,179));
    }
    void DrawLane(Graphics g,short[] data,int start,int length,int top,int height,string title,Color color) {
        float scale=Math.Max(1,(height-36)/2f), mid=top+26+scale;
        using(var grid=new Pen(Color.FromArgb(54,65,82))) {
            g.DrawLine(grid,0,mid,Width,mid); g.DrawLine(grid,0,mid-scale,Width,mid-scale); g.DrawLine(grid,0,mid+scale,Width,mid+scale);
        }
        using(var brush=new SolidBrush(color)) g.DrawString(title+(data==null?"  — 未取得":string.Format("  {0:F3} ～ {1:F3} 秒",start/48000.0,(start+length)/48000.0)),Font,brush,10,top+3);
        if(data==null || data.Length==0) return;
        using(var pen=new Pen(color,1.4f)) {
            PointF? previous=null;
            for(int x=0;x<Width;x++) {
                int a=start+(int)((long)x*length/Width), b=Math.Min(data.Length,start+(int)((long)(x+1)*length/Width)+1);
                if(b<=0 || a>=data.Length) { previous=null; continue; }
                a=Math.Max(0,a);
                short low=short.MaxValue, high=short.MinValue;
                for(int i=a;i<b;i++) { low=Math.Min(low,data[i]); high=Math.Max(high,data[i]); }
                float y=mid-data[a]/32768f*scale;
                if(previous.HasValue) g.DrawLine(pen,previous.Value,new PointF(x,y));
                g.DrawLine(pen,x,mid-high/32768f*scale,x,mid-low/32768f*scale); previous=new PointF(x,y);
            }
        }
    }
}

public sealed class MainForm : Form {
    ComboBox input=new ComboBox(), output=new ComboBox(), monitorOutput=new ComboBox();
    Button pauseButton=new Button(); MeasurementPause pauseControl;
    TrackBar monitorVolumeSlider=new TrackBar(); Label monitorVolumeLabel=new Label(); MonitorVolume monitorLevel=new MonitorVolume();
    TextBox file=new TextBox(); NumericUpDown speed=new NumericUpDown(), gain=new NumericUpDown(), duration=new NumericUpDown(), zoom=new NumericUpDown(), position=new NumericUpDown();
    Button browse=new Button(), start=new Button(), stop=new Button(), save=new Button(), peakButton=new Button();
    Label status=new Label(), numbers=new Label(); ProgressBar meter=new ProgressBar(); WaveView wave=new WaveView();
    TextBox comparison=new TextBox();
    ComboBox waveChannel=new ComboBox(); CheckBox alignWaves=new CheckBox();
    CheckBox wholeFile=new CheckBox(), autoGain=new CheckBox(); Label appliedGain=new Label(); bool loadingSettings=true;
    readonly string settingsPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"settings.xml");
    AppSettings preferences=new AppSettings();
    ComboBox recognitionModel=new ComboBox(); TextBox recognitionEngine=new TextBox(); Button chooseEngine=new Button(), inspectRecording=new Button(), playOnly=new Button();
    byte[] playbackPcm; ComparisonResult comparisonResult;
    CancellationTokenSource cancellation; CaptureResult result; string settings=""; bool busy;
    public MainForm() : this(null) { }
    public MainForm(string configurationPath) {
        if(configurationPath!=null) settingsPath=configurationPath;
        Text="PGA-TapeStudio — テープWAV再生・録音検査"; ClientSize=new Size(1080,940); MinimumSize=new Size(1020,900);
        Font=new Font("Yu Gothic UI",10); BackColor=Color.FromArgb(244,247,251);
        var layout=new TableLayoutPanel { Dock=DockStyle.Fill, Padding=new Padding(24), ColumnCount=1, RowCount=14 };
        Controls.Add(layout);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,44)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,24)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,64));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute,140));
        layout.RowStyles.Insert(7,new RowStyle(SizeType.Absolute,42));
        layout.Controls.Add(new Label { Text="PGA-TapeStudio", Font=new Font(Font.FontFamily,21,FontStyle.Bold), AutoSize=true });
        layout.Controls.Add(new Label { Text="LINE OUT → 抵抗入りケーブル → MIC IN\n出力音量を小さくして開始。Windowsのマイクブースト・自動ゲイン・音声補正を確認してください。", AutoSize=true });
        input.Width=360; output.Width=250; input.DropDownStyle=output.DropDownStyle=monitorOutput.DropDownStyle=ComboBoxStyle.DropDownList;
        layout.Controls.Add(Row(Label("録音入力",90),input,Label("48 kHz / 16 bit / モノラル",260)));
        monitorOutput.Width=210; monitorOutput.Items.Add("なし"); monitorOutput.SelectedIndex=0;
        monitorVolumeSlider.Minimum=0; monitorVolumeSlider.Maximum=100; monitorVolumeSlider.Value=100; monitorVolumeSlider.Width=110; monitorVolumeSlider.TickFrequency=25;
        monitorVolumeSlider.AccessibleName="モニター音量"; monitorVolumeLabel.Width=50; monitorVolumeLabel.Text="100%";
        monitorVolumeSlider.ValueChanged+=delegate { monitorLevel.Percent=monitorVolumeSlider.Value; monitorVolumeLabel.Text=monitorVolumeSlider.Value+"%"; PersistSettings(); };
        layout.Controls.Add(Row(Label("再生出力",90),output,Label("モニター",80),monitorOutput,Label("音量",45),monitorVolumeSlider,monitorVolumeLabel));
        file.Width=650; file.ReadOnly=true; browse.Text="音声を選択"; browse.AutoSize=true; browse.Click+=Browse;
        layout.Controls.Add(Row(file,browse));
        SetupNumber(speed,0.5m,4,2,0.25m,2); SetupNumber(gain,-60,0,-18,1,0); SetupNumber(duration,1,86400,15,1,0);
        wholeFile.Text="全部"; wholeFile.AutoSize=true; wholeFile.Checked=true; duration.Enabled=false;
        wholeFile.CheckedChanged+=delegate { duration.Enabled=!busy && !wholeFile.Checked; };
        autoGain.Text="自動ゲイン（−1 dBFS）"; autoGain.AutoSize=true;
        appliedGain.Text=""; appliedGain.Width=145;
        autoGain.CheckedChanged+=delegate { gain.Enabled=!busy && !autoGain.Checked; appliedGain.Text=autoGain.Checked?"再生前に全体解析":""; };
        layout.Controls.Add(Row(Label("測定速度",72),speed,Label("倍  出力",72),gain,Label("dB  測定",78),duration,Label("秒",28),wholeFile,autoGain,appliedGain));
        start.Text="再生して測定"; start.Width=160; stop.Text="停止"; stop.Enabled=false; save.Text="WAV・結果を保存"; save.Width=165; save.Enabled=false;
        start.Click+=Start; stop.Click+=delegate { if(cancellation!=null) cancellation.Cancel(); }; save.Click+=Save;
        pauseButton.Text="一時停止"; pauseButton.Width=110; pauseButton.Enabled=false;
        pauseButton.Click+=delegate {
            if(pauseControl==null) return;
            pauseControl.Requested=!pauseControl.Requested;
            pauseButton.Text=pauseControl.Requested?"再開":"一時停止";
            status.Text=pauseControl.Requested?"一時停止を要求しています…":"再開を要求しています…";
        };
        inspectRecording.Text="保存WAVを認識検査"; inspectRecording.Width=185; inspectRecording.Click+=InspectRecording;
        playOnly.Text="再生のみ（1倍）"; playOnly.Width=140; playOnly.Click+=PlayOnly;
        layout.Controls.Add(Row(playOnly,start,pauseButton,stop,save,inspectRecording));
        recognitionModel.DropDownStyle=ComboBoxStyle.DropDownList; recognitionModel.Width=170; recognitionModel.Items.Add("認識検査なし"); recognitionModel.Items.AddRange(TapeRecognition.Models); recognitionModel.SelectedIndex=0;
        recognitionEngine.Width=470; recognitionEngine.ReadOnly=true; chooseEngine.Text="エンジン選択"; chooseEngine.Width=115;
        chooseEngine.Click+=delegate { using(var d=new OpenFileDialog { Filter="DumpListEditor|DumpListEditor.exe" }) if(d.ShowDialog()==DialogResult.OK) recognitionEngine.Text=d.FileName; };
        layout.Controls.Add(Row(Label("データ認識",90),recognitionModel,recognitionEngine,chooseEngine));
        status.Text="音声ファイルと、実際に接続した入出力デバイスを選んでください。"; status.Dock=DockStyle.Fill; status.Font=new Font(Font,FontStyle.Bold); layout.Controls.Add(status);
        meter.Dock=DockStyle.Fill; meter.Maximum=600; layout.Controls.Add(meter);
        numbers.Text="最大ピーク — dBFS    RMS — dBFS"; numbers.Dock=DockStyle.Fill; layout.Controls.Add(numbers);
        wave.Dock=DockStyle.Fill; layout.Controls.Add(wave);
        SetupNumber(zoom,1,2000,50,10,0); SetupNumber(position,0,130,0,0.01m,3); position.Width=100;
        peakButton.Text="最大ピークへ"; peakButton.Width=125;
        peakButton.Click+=delegate { if(result!=null) position.Value=Math.Min(position.Maximum,(decimal)result.Stats.PeakIndex/48000); };
        zoom.ValueChanged+=delegate { wave.WindowMs=(int)zoom.Value; wave.Invalidate(); };
        position.ValueChanged+=delegate { wave.Center=(int)(position.Value*48000); wave.Invalidate(); };
        waveChannel.DropDownStyle=ComboBoxStyle.DropDownList; waveChannel.Width=110;
        waveChannel.Items.AddRange(new object[] { "左", "右", "左右平均" }); waveChannel.SelectedIndex=0;
        waveChannel.SelectedIndexChanged+=delegate { UpdateWaves(); };
        alignWaves.Text="遅延を合わせる"; alignWaves.AutoSize=true; alignWaves.Checked=true; alignWaves.Enabled=false;
        alignWaves.CheckedChanged+=delegate { UpdateWaves(); };
        layout.Controls.Add(Row(Label("表示幅",62),zoom,Label("ms   位置",80),position,Label("秒",25),peakButton,Label("出力",45),waveChannel,alignWaves));
        comparison.Multiline=true; comparison.ReadOnly=true; comparison.ScrollBars=ScrollBars.Vertical; comparison.Dock=DockStyle.Fill;
        comparison.Text="測定後、波形相似度の平均・最低値と区間別スコアを表示します（95%未満は要確認）。"; layout.Controls.Add(comparison);
        try {
            for(uint i=0;i<Native.waveInGetNumDevs();i++) input.Items.Add(Native.DeviceName(i,true));
            for(uint i=0;i<Native.waveOutGetNumDevs();i++) { string name=Native.DeviceName(i,false); output.Items.Add(name); monitorOutput.Items.Add(name); }
            if(input.Items.Count>0) input.SelectedIndex=0; if(output.Items.Count>0) output.SelectedIndex=0;
            Decoder.FindFfmpeg();
        } catch(Exception ex) { status.Text=ex.Message; }
        RestoreSettings(); loadingSettings=false;
        foreach(var n in new NumericUpDown[] { speed,gain,duration,zoom }) n.ValueChanged+=delegate { PersistSettings(); };
        foreach(var c in new ComboBox[] { input,output,monitorOutput,waveChannel,recognitionModel }) c.SelectedIndexChanged+=delegate { PersistSettings(); };
        foreach(var c in new CheckBox[] { wholeFile,alignWaves,autoGain }) c.CheckedChanged+=delegate { PersistSettings(); };
        file.TextChanged+=delegate { PersistSettings(); };
        recognitionEngine.TextChanged+=delegate { PersistSettings(); };
        EnableFileDrop(this);
        FormClosing+=delegate(object sender,FormClosingEventArgs e) { if(busy) { cancellation.Cancel(); e.Cancel=true; status.Text="測定を停止しています。停止後に閉じてください。"; } else PersistSettings(); };
    }
    static string[] DeviceNames(ComboBox box) { var names=new string[box.Items.Count]; for(int i=0;i<names.Length;i++) names[i]=box.Items[i].ToString(); return names; }
    void RestoreSettings() {
        try {
            preferences=AppSettings.Load(settingsPath);
            input.SelectedIndex=AppSettings.FindDevice(DeviceNames(input),preferences.InputName);
            output.SelectedIndex=AppSettings.FindDevice(DeviceNames(output),preferences.OutputName);
            int savedMonitor=preferences.MonitorEnabled?AppSettings.FindDevice(DeviceNames(output),preferences.MonitorName):-1;
            monitorOutput.SelectedIndex=preferences.MonitorEnabled?(savedMonitor<0?-1:savedMonitor+1):0;
            monitorVolumeSlider.Value=preferences.MonitorVolumePercent;
            speed.Value=preferences.Speed; gain.Value=preferences.Gain; duration.Value=preferences.Seconds; zoom.Value=preferences.Zoom;
            wholeFile.Checked=preferences.WholeFile; alignWaves.Checked=preferences.AlignWaves;
            autoGain.Checked=preferences.AutoGain;
            waveChannel.SelectedIndex=preferences.WaveChannel;
            recognitionModel.SelectedIndex=Math.Max(0,Array.IndexOf(TapeRecognition.Models,preferences.RecognitionModel)+1);
            recognitionEngine.Text=preferences.RecognitionEngine;
            if(string.IsNullOrEmpty(recognitionEngine.Text)) {
                string found=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),@"ChatGPT\SKYKID80\build\DumpListEditorVer084\DumpListEditorVer084\DumpListEditor.exe");
                if(File.Exists(found)) recognitionEngine.Text=found;
            }
            if(File.Exists(preferences.LastFile)) file.Text=preferences.LastFile;
            if(input.SelectedIndex<0 || output.SelectedIndex<0 || monitorOutput.SelectedIndex<0) status.Text="保存した入出力が見つからないか同名です。使用するデバイスを選択してください。";
        } catch(Exception ex) { status.Text="設定を読み込めません。初期値を使用します："+ex.Message; }
    }
    void PersistSettings() {
        if(loadingSettings) return;
        if(input.SelectedIndex>=0) preferences.InputName=input.Text;
        if(output.SelectedIndex>=0) preferences.OutputName=output.Text;
        if(monitorOutput.SelectedIndex>=0) { preferences.MonitorEnabled=monitorOutput.SelectedIndex>0; preferences.MonitorName=preferences.MonitorEnabled?monitorOutput.Text:""; }
        preferences.LastFile=file.Text; preferences.Speed=speed.Value; preferences.Gain=gain.Value; preferences.Seconds=duration.Value;
        preferences.Zoom=zoom.Value; preferences.WholeFile=wholeFile.Checked;
        preferences.AutoGain=autoGain.Checked;
        preferences.AlignWaves=alignWaves.Checked; preferences.WaveChannel=waveChannel.SelectedIndex;
        preferences.RecognitionEngine=recognitionEngine.Text; preferences.RecognitionModel=recognitionModel.SelectedIndex>0?recognitionModel.Text:"";
        preferences.MonitorVolumePercent=monitorVolumeSlider.Value;
        try { preferences.Save(settingsPath); } catch(Exception ex) { status.Text="設定を保存できません："+ex.Message; }
    }
    void EnableFileDrop(Control control) {
        control.AllowDrop=true;
        control.DragEnter+=delegate(object sender,DragEventArgs e) {
            string path=e.Data.GetDataPresent(DataFormats.FileDrop)?AppSettings.DroppedFile(e.Data.GetData(DataFormats.FileDrop) as string[]):null;
            e.Effect=!busy && path!=null && (e.AllowedEffect & DragDropEffects.Copy)!=0?DragDropEffects.Copy:DragDropEffects.None;
        };
        control.DragDrop+=delegate(object sender,DragEventArgs e) {
            if(busy) return;
            string path=e.Data.GetDataPresent(DataFormats.FileDrop)?AppSettings.DroppedFile(e.Data.GetData(DataFormats.FileDrop) as string[]):null;
            if(path!=null) { file.Text=path; status.Text="音声ファイルを選択しました。「再生のみ」または「再生して測定」で開始します。"; }
        };
        foreach(Control child in control.Controls) EnableFileDrop(child);
    }
    static Label Label(string text,int width) { return new Label { Text=text, Width=width, AutoSize=false, Height=30, TextAlign=ContentAlignment.MiddleLeft }; }
    static FlowLayoutPanel Row(params Control[] controls) { var row=new FlowLayoutPanel { Dock=DockStyle.Fill, Margin=Padding.Empty }; row.Controls.AddRange(controls); return row; }
    static void SetupNumber(NumericUpDown n,decimal min,decimal max,decimal value,decimal step,int decimals) { n.Minimum=min; n.Maximum=max; n.Value=value; n.Increment=step; n.DecimalPlaces=decimals; n.Width=75; }
    void UpdateWaves() {
        wave.Playback=WaveView.ExtractPlayback(playbackPcm,waveChannel.SelectedIndex);
        wave.OutputChannel=waveChannel.Text;
        wave.Aligned=comparisonResult!=null && comparisonResult.Aligned && alignWaves.Checked;
        wave.DelaySamples=comparisonResult==null?0:(int)Math.Round(comparisonResult.DelayMs*48);
        wave.Invalidate();
    }
    void Browse(object sender,EventArgs e) { using(var d=new OpenFileDialog { Filter="音声ファイル|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.ogg;*.wma;*.aiff;*.opus|すべてのファイル|*.*" }) if(d.ShowDialog()==DialogResult.OK) file.Text=d.FileName; }
    void SetBusy(bool value) {
        busy=value; playOnly.Enabled=start.Enabled=browse.Enabled=input.Enabled=output.Enabled=monitorOutput.Enabled=speed.Enabled=gain.Enabled=wholeFile.Enabled=!value;
        autoGain.Enabled=!value; gain.Enabled=!value && !autoGain.Checked;
        if(!value) { pauseButton.Enabled=false; pauseButton.Text="一時停止"; }
        duration.Enabled=!value && !wholeFile.Checked;
        recognitionModel.Enabled=recognitionEngine.Enabled=chooseEngine.Enabled=inspectRecording.Enabled=!value;
        stop.Enabled=value; save.Enabled=!value && result!=null;
    }
    async void PlayOnly(object sender,EventArgs e) {
        if(!File.Exists(file.Text) || output.SelectedIndex<0 || monitorOutput.SelectedIndex<0) { MessageBox.Show(this,"音源と再生出力を選んでください。モニター不要の場合は「なし」を選びます。"); return; }
        uint outId=(uint)output.SelectedIndex; int monitorId=monitorOutput.SelectedIndex-1;
        try { AudioEngine.ValidateOutputs(outId,monitorId); } catch(Exception ex) { MessageBox.Show(this,ex.Message); return; }
        string path=file.Text; double level=(double)gain.Value; int seconds=wholeFile.Checked?0:(int)duration.Value; bool automatic=autoGain.Checked;
        SetBusy(true); cancellation=new CancellationTokenSource(); result=null; comparisonResult=null; playbackPcm=null;
        wave.Samples=null; alignWaves.Enabled=false; meter.Value=0; UpdateWaves();
        numbers.Text="再生のみ：100%（1倍速）固定 / 録音・レベル測定なし"; comparison.Text="再生のみでは録音・波形比較・認識検査は行いません。測定速度の設定にかかわらず100%で再生します。";
        try {
            status.Text="再生用の音声を準備しています…";
            var prepared=await Task.Run(()=>Decoder.PrepareForPlayback(path,level,seconds,automatic,cancellation.Token));
            byte[] pcm=prepared.Pcm; appliedGain.Text=automatic?prepared.GainDb.ToString("+0.0;-0.0;0.0")+" dB":"";
            comparison.Text+= "\r\n"+prepared.GainDescription;
            playbackPcm=pcm; position.Maximum=Math.Max(130,(decimal)pcm.Length/192000+10); position.Value=0; UpdateWaves();
            pauseControl=new MeasurementPause(); pauseButton.Enabled=true;
            var played=await Task.Run(()=>AudioEngine.Run(pcm,0,outId,cancellation.Token,(a,t)=> {
                BeginInvoke((Action)(()=> { status.Text=string.Format("{0} {1:F1} / {2:F1} 秒",pauseControl!=null && pauseControl.IsPaused?"一時停止中":"再生中",t,pcm.Length/192000.0); position.Value=Math.Min(position.Maximum,(decimal)t); }));
            },monitorId,pauseControl,false,monitorLevel));
            status.Text=played.Cancelled?"再生を停止しました。":"再生が完了しました。";
        } catch(OperationCanceledException) { status.Text="再生を停止しました。"; }
        catch(Exception ex) { status.Text="再生できませんでした。"; MessageBox.Show(this,ex.Message,"再生エラー",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        finally { cancellation.Dispose(); cancellation=null; pauseControl=null; SetBusy(false); }
    }
    async void Start(object sender,EventArgs e) {
        if(!File.Exists(file.Text) || input.SelectedIndex<0 || output.SelectedIndex<0 || monitorOutput.SelectedIndex<0) { MessageBox.Show(this,"音声ファイルと入出力デバイスを選んでください。モニター不要の場合は「なし」を選びます。"); return; }
        int monitorId=monitorOutput.SelectedIndex-1;
        if(recognitionModel.SelectedIndex>0 && !File.Exists(recognitionEngine.Text)) { MessageBox.Show(this,"DumpListEditor.exeを選択してください。"); return; }
        try { AudioEngine.ValidateOutputs((uint)output.SelectedIndex,monitorId); } catch(Exception ex) { MessageBox.Show(this,ex.Message); return; }
        SetBusy(true); cancellation=new CancellationTokenSource(); result=null; wave.Samples=null; wave.Invalidate(); meter.Value=0;
        playbackPcm=null; comparisonResult=null; alignWaves.Enabled=false; UpdateWaves();
        comparison.Text="比較はまだ実行していません。";
        string path=file.Text; double rate=(double)speed.Value, level=(double)gain.Value; int seconds=wholeFile.Checked?0:(int)duration.Value; bool keep=false, automatic=autoGain.Checked;
        uint inId=(uint)input.SelectedIndex, outId=(uint)output.SelectedIndex;
        settings="日時: "+DateTime.Now.ToString("O")+"\r\n音源: "+path+"\r\n入力: "+input.Text+"\r\n出力: "+output.Text+"\r\n速度: "+rate+" 倍\r\n出力ゲイン: "+level+" dB\r\n測定上限: "+(seconds==0?"全部":seconds+" 秒")+"\r\n音程維持: "+keep+"\r\n";
        settings+="モニター出力: "+monitorOutput.Text+"\r\n";
        try {
            status.Text="音声を準備しています…";
            var prepared=await Task.Run(()=>Decoder.Prepare(path,rate,level,seconds,automatic,keep,cancellation.Token));
            byte[] pcm=prepared.Pcm;
            settings=settings.Replace("出力ゲイン: "+level+" dB","出力ゲイン: "+prepared.GainDb.ToString(CultureInfo.InvariantCulture)+" dB");
            settings+="自動ゲイン: "+automatic+"\r\n"+prepared.GainDescription+"\r\n";
            appliedGain.Text=automatic?prepared.GainDb.ToString("+0.0;-0.0;0.0")+" dB":"";
            position.Maximum=Math.Max(130,(decimal)pcm.Length/192000+10);
            playbackPcm=pcm; UpdateWaves();
            status.Text="録音・倍速再生中…";
            pauseControl=new MeasurementPause(); pauseButton.Enabled=true;
            result=await Task.Run(()=>AudioEngine.Run(pcm,inId,outId,cancellation.Token,(a,t)=> {
                string text=Metrics(a); int value=(int)Math.Max(0,Math.Min(600,(a.PeakDb+60)*10));
                BeginInvoke((Action)(()=> { numbers.Text=text; meter.Value=value; status.Text=string.Format("{0} {1:F1} / 約{2:F1} 秒  /  {3}",pauseControl!=null && pauseControl.IsPaused?"一時停止中":"測定中",t,pcm.Length/192000.0+0.8,a.Verdict); }));
            },monitorId,pauseControl,true,monitorLevel));
            pauseButton.Enabled=false;
            settings+="一時停止回数: "+result.PauseCount+"\r\n";
            wave.Samples=result.Audio; position.Value=Math.Min(position.Maximum,(decimal)result.Stats.PeakIndex/48000); wave.Center=result.Stats.PeakIndex; wave.Invalidate();
            status.Text="再生データと録音を比較しています…";
            if(!result.Cancelled && result.PauseCount==0) {
                var compared=await Task.Run(()=>SignalComparison.Compare(pcm,result.Audio,cancellation.Token));
                comparison.Text=compared.Report();
                comparisonResult=compared; alignWaves.Enabled=compared.Aligned;
                if(compared.Aligned) waveChannel.SelectedIndex=compared.Channel=="左チャンネル"?0:compared.Channel=="右チャンネル"?1:2;
                UpdateWaves();
            } else comparison.Text=result.PauseCount>0?"一時停止を含む録音のため、自動比較は実行していません。停止・再開の境界を音飛びと誤判定しないためです。波形・ピーク確認と録音保存は利用できます。":"測定を中断したため、自動比較は実行していません。";
            comparison.Text=prepared.GainDescription+"\r\n"+comparison.Text;
            numbers.Text=Metrics(result.Stats); status.Text=(result.Cancelled?"中断 / ":"完了 / ")+result.Stats.Verdict;
            if(result.PossibleGap) status.Text="録音欠落の可能性あり — 他の処理を止めて再測定してください。";
            if(recognitionModel.SelectedIndex>0 && !result.Cancelled && result.PauseCount==0) {
                string model=recognitionModel.Text, engine=recognitionEngine.Text; int channel=waveChannel.SelectedIndex; bool all=wholeFile.Checked;
                status.Text="DumpListEditorで元音源・再生データ・録音を認識しています…";
                string recognized=await Task.Run(()=>TapeRecognition.Check(path,pcm,result.Audio,engine,model,rate,keep,channel,all,cancellation.Token));
                comparison.Text=recognized+"\r\n\r\n[波形比較]\r\n"+comparison.Text;
                status.Text=result.PossibleGap?"録音欠落の可能性あり。認識検査結果を確認し、再測定してください。":"測定・認識検査完了。結果欄を確認してください。";
            }
            wave.Samples=result.Audio; position.Value=Math.Min(position.Maximum,(decimal)result.Stats.PeakIndex/48000); wave.Center=result.Stats.PeakIndex; wave.Invalidate();
        } catch(OperationCanceledException) { status.Text="測定・比較をキャンセルしました。"; comparison.Text="自動比較は完了していません。"; }
        catch(OutOfMemoryException) { status.Text="メモリが不足しました。測定秒数を指定するか、音源を分割してください。"; comparison.Text="メモリ不足のため測定・比較は完了していません。"; }
        catch(Exception ex) { status.Text="測定できませんでした。"; MessageBox.Show(this,ex.Message,"測定エラー",MessageBoxButtons.OK,MessageBoxIcon.Error); }
        finally { cancellation.Dispose(); cancellation=null; pauseControl=null; SetBusy(false); }
    }
    static string Metrics(Analysis a) { return string.Format("最大 {0:F2} dBFS   RMS {1:F2} dBFS   上限付近 {2:N0} samples   レール付近 {3:N0}   平坦部 {4:N0}",a.PeakDb,a.RmsDb,a.NearFullScale,a.RailSamples,a.FlatRuns); }
    async void InspectRecording(object sender,EventArgs e) {
        if(!File.Exists(file.Text) || recognitionModel.SelectedIndex<=0 || !File.Exists(recognitionEngine.Text)) { MessageBox.Show(this,"元音源・認識機種・DumpListEditor.exeを選択してください。録音の付属ログがない場合は速度・出力ゲイン・測定範囲を録音時に合わせてください。"); return; }
        string recorded;
        using(var d=new OpenFileDialog { Title="検査する録音WAVを選択", Filter="録音WAV|*.wav" }) { if(d.ShowDialog()!=DialogResult.OK) return; recorded=d.FileName; }
        string source=file.Text,engine=recognitionEngine.Text,model=recognitionModel.Text; double rate=(double)speed.Value,level=(double)gain.Value; bool keep=false,all=wholeFile.Checked,automatic=autoGain.Checked; int seconds=all?0:(int)duration.Value,channel=waveChannel.SelectedIndex;
        if(File.Exists(recorded+".txt")) {
            try { TapeRecognition.ReadRecordedSettings(recorded+".txt",ref rate,ref level,ref keep,ref seconds); all=seconds==0; automatic=File.ReadAllText(recorded+".txt").Contains("自動ゲイン: True"); }
            catch(Exception ex) { MessageBox.Show(this,"録音ログの条件を読み込めません："+ex.Message); return; }
        }
        SetBusy(true); cancellation=new CancellationTokenSource();
        try {
            status.Text="保存WAVの認識検査中…";
            string report=await Task.Run(()=> {
                var prepared=Decoder.Prepare(source,rate,level,seconds,automatic,keep,cancellation.Token); var pcm=prepared.Pcm;
                var audio=WaveView.ExtractPlayback(Decoder.Decode(recorded,1,0,0,false,cancellation.Token),0);
                return "元音源: "+source+"\r\n録音: "+recorded+"\r\n速度: "+rate+" / 音程維持: "+keep+"\r\n"+prepared.GainDescription+"\r\n"+TapeRecognition.Check(source,pcm,audio,engine,model,rate,keep,channel,all,cancellation.Token);
            });
            using(var dialog=new Form { Text="保存WAVのデータ認識結果", Width=1000, Height=750, StartPosition=FormStartPosition.CenterParent }) {
                var box=new TextBox { Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Both, Dock=DockStyle.Fill, Text=report };
                var export=new Button { Text="認識結果を保存", Dock=DockStyle.Bottom, Height=36 };
                export.Click+=delegate { using(var d=new SaveFileDialog { Filter="検査結果|*.txt", FileName=Path.GetFileName(recorded)+".recognition.txt" }) if(d.ShowDialog()==DialogResult.OK) { try { File.WriteAllText(d.FileName,report,new UTF8Encoding(true)); } catch(Exception ex) { MessageBox.Show(dialog,ex.Message); } } };
                dialog.Controls.Add(box); dialog.Controls.Add(export); dialog.ShowDialog(this);
            }
            status.Text="保存WAVの認識検査が完了しました。";
        } catch(OperationCanceledException) { status.Text="認識検査を中断しました。"; }
        catch(Exception ex) { status.Text="認識検査できませんでした。"; MessageBox.Show(this,ex.Message); }
        finally { cancellation.Dispose(); cancellation=null; SetBusy(false); }
    }
    void Save(object sender,EventArgs e) {
        using(var d=new SaveFileDialog { Filter="録音 WAV|*.wav", FileName="mic-check-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".wav" }) {
            if(d.ShowDialog()!=DialogResult.OK) return;
            try {
                string report=d.FileName+".txt";
                if(File.Exists(report) && MessageBox.Show(this,"結果ファイルも上書きしますか？\n"+report,"上書き確認",MessageBoxButtons.YesNo)!=DialogResult.Yes) return;
                AudioEngine.SaveWave(d.FileName,result.Audio);
                File.WriteAllText(report,settings+"録音秒数: "+(result.Audio.Length/48000.0).ToString("F3")+"\r\n中断: "+result.Cancelled+"\r\n欠落の可能性: "+result.PossibleGap+"\r\n"+Metrics(result.Stats)+"\r\n"+result.Stats.Verdict+"\r\n平坦部は5サンプル以上、隣接差2 LSB以下、絶対振幅0.1以上の区間です。参考判定であり、アナログ歪みの有無や原因を確定できません。\r\n\r\n"+comparison.Text,new UTF8Encoding(true));
                status.Text="録音と結果を保存しました。";
            } catch(Exception ex) { MessageBox.Show(this,ex.Message,"保存エラー"); }
        }
    }
}
}
