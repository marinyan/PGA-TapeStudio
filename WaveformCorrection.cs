using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RecordingLevelChecker {
public static class WaveformCorrection {
    public const string None="無編集";
    public const string Version="logic-v1";
    public const string CircuitVersion="save-rc-v1";
    public static bool CircuitSupported(string model) {
        return model=="PC_6001" || model=="PC_6601SR" || model=="PC_8801" || model=="PC_8801mkIISR" || model=="MSX";
    }
    public static string VersionFor(string model) { return CircuitSupported(model)?CircuitVersion:Version; }
    public static bool Supported(string model) {
        return CircuitSupported(model) || model=="MZ_700" || model=="X1" || model=="FM_7" || model=="PASOPIA7" || model=="PYUTA" || model=="FP_1100" || model=="BML3";
    }
    public static string Validate(string model) {
        if(string.IsNullOrEmpty(model) || model==None) return None;
        if(Array.IndexOf(TapeRecognition.Models,model)<0) throw new ArgumentException("不明な波形補正機種: "+model);
        return model;
    }
    public static string Description(string model) {
        model=Validate(model);
        if(CircuitSupported(model)) return (model=="MSX"?"MSX標準例":"SAVE回路近似")+"：矩形化＋"+(model.StartsWith("PC_88")?"RC低域通過":"AC結合・RC低域通過")+"／相対波形・実機ロード未検証";
        return model==None?"波形補正なし（ゲイン・位相反転は別設定）":Supported(model)?"試験的な論理波形近似。符号区間を矩形化／実機のアナログ回路は未再現":"未補正：出力回路の裏付けが不足しているため元波形を使用";
    }
    public static string ReadLog(string path) {
        string model=None,version=null;
        foreach(string line in File.ReadAllLines(path)) {
            if(line.StartsWith("波形補正機種: ")) model=Validate(line.Substring("波形補正機種: ".Length));
            if(line.StartsWith("波形補正方式: ")) version=line.Substring("波形補正方式: ".Length);
        }
        // These profiles were no-ops in logic-v1. Never apply the new circuit to an old capture.
        if(CircuitSupported(model) && version==Version) return None;
        if(model!=None && version!=VersionFor(model)) throw new InvalidDataException("波形補正方式が一致しないため再現できません。");
        return model;
    }
    // Rectangularization preserves sampled signs/zeros; the optional circuit then changes phase.
    // Local peaks avoid expanding quiet gaps/noise to the loudest peak in the file.
    public static void Apply(byte[] pcm,string model,CancellationToken token) {
        Validate(model);
        if(pcm==null || pcm.Length%4!=0) throw new ArgumentException("ステレオPCMが不正です。");
        token.ThrowIfCancellationRequested();
        if(!Supported(model)) return;
        for(int channel=0;channel<2;channel++) {
            int frame=0,count=pcm.Length/4;
            while(frame<count) {
                token.ThrowIfCancellationRequested();
                int start=frame,first=Read(pcm,frame*4+channel*2),sign=Math.Sign(first),peak=Math.Abs(first);
                frame++;
                if(sign==0) continue;
                while(frame<count) {
                    if((frame&16383)==0) token.ThrowIfCancellationRequested();
                    int value=Read(pcm,frame*4+channel*2);
                    if(Math.Sign(value)!=sign) break;
                    peak=Math.Max(peak,Math.Abs(value)); frame++;
                }
                // Long DC regions are not interpreted as tape pulses (10 ms at 48 kHz).
                if(frame-start>480) continue;
                short shaped=(short)(sign*peak);
                for(int i=start;i<frame;i++) { int offset=i*4+channel*2; pcm[offset]=(byte)shaped; pcm[offset+1]=(byte)(shaped>>8); }
            }
        }
        if(CircuitSupported(model)) ApplyCircuit(pcm,model,token);
    }
    // Ideal voltage source, unloaded connector. R2+R3 loads the shunt capacitor.
    // Normalize out resistor attenuation: PCM amplitude is not a calibrated voltage.
    // See docs/save-circuit-research.md for the component values and source diagrams.
    static void ApplyCircuit(byte[] pcm,string model,CancellationToken token) {
        double r1,r2,r3,c1,c2;
        if(model=="MSX") { r1=4700; r2=4700; r3=100; c1=1e-6; c2=22e-9; }
        else if(model.StartsWith("PC_88")) { r1=560; r2=5100; r3=33; c1=0; c2=100e-9; }
        else { r1=2200; r2=12000; r3=100; c1=100e-9; c2=47e-9; }
        double load=r2+r3,k=96000,b0,b1,b2,a0,a1,a2;
        if(c1==0) {
            double t=r1*load/(r1+load)*c2;
            a0=1+t*k; a1=1-t*k; a2=0; b0=1; b1=1; b2=0;
        } else {
            // H(s) = n*s / (1 + d*s + e*s*s), bilinear transform at 48 kHz.
            double n=c1*(r1+load),d=n+c2*load,e=c1*c2*r1*load;
            a0=1+d*k+e*k*k; a1=2-2*e*k*k; a2=1-d*k+e*k*k;
            b0=n*k; b1=0; b2=-n*k;
        }
        b0/=a0; b1/=a0; b2/=a0; a1/=a0; a2/=a0;
        double sourcePeak=0,outputPeak=0,scale=1;
        // First pass finds a linked stereo safety scale without quantizing/clipping filter state.
        // Second pass writes PCM. No extra full-length floating-point buffer is needed.
        for(int pass=0;pass<2;pass++) {
            for(int channel=0;channel<2;channel++) {
                double x1=0,x2=0,y1=0,y2=0;
                for(int offset=channel*2;offset<pcm.Length;offset+=4) {
                    if((offset&65532)==0) token.ThrowIfCancellationRequested();
                    double x=Read(pcm,offset),y=b0*x+b1*x1+b2*x2-a1*y1-a2*y2;
                    x2=x1; x1=x; y2=y1; y1=y;
                    if(pass==0) { sourcePeak=Math.Max(sourcePeak,Math.Abs(x)); outputPeak=Math.Max(outputPeak,Math.Abs(y)); }
                    else {
                        short value=(short)Math.Max(short.MinValue,Math.Min(short.MaxValue,Math.Round(y*scale)));
                        pcm[offset]=(byte)value; pcm[offset+1]=(byte)(value>>8);
                    }
                }
            }
            if(pass==0 && outputPeak>0) scale=Math.Min(1,Math.Min(sourcePeak,32767)/outputPeak);
        }
    }
    static int Read(byte[] pcm,int offset) { return (short)(pcm[offset]|pcm[offset+1]<<8); }
    public static void Gain(byte[] pcm,double db,CancellationToken token) {
        double factor=Math.Pow(10,db/20);
        for(int i=0;i<pcm.Length;i+=2) {
            if((i&65535)==0) token.ThrowIfCancellationRequested();
            short value=(short)Math.Max(short.MinValue,Math.Min(short.MaxValue,Math.Round(Read(pcm,i)*factor)));
            pcm[i]=(byte)value; pcm[i+1]=(byte)(value>>8);
        }
    }
    public static void SaveWave(string path,byte[] pcm) {
        if(pcm==null || pcm.Length%4!=0) throw new ArgumentException("ステレオPCMが不正です。");
        using(var writer=new BinaryWriter(File.Create(path))) {
            writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write((uint)pcm.Length+36);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)2);
            writer.Write(48000); writer.Write(192000); writer.Write((short)4); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
        }
    }
}
}
