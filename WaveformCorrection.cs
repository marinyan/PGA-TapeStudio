using System;
using System.IO;
using System.Text;
using System.Threading;

namespace RecordingLevelChecker {
public static class WaveformCorrection {
    public const string None="無編集";
    public const string Version="logic-v1";
    public static bool Supported(string model) {
        return model=="MZ_700" || model=="X1" || model=="FM_7" || model=="PASOPIA7" || model=="PYUTA" || model=="FP_1100" || model=="BML3";
    }
    public static string Validate(string model) {
        if(string.IsNullOrEmpty(model) || model==None) return None;
        if(Array.IndexOf(TapeRecognition.Models,model)<0) throw new ArgumentException("不明な波形補正機種: "+model);
        return model;
    }
    public static string Description(string model) {
        model=Validate(model);
        return model==None?"波形補正なし（ゲイン・位相反転は別設定）":Supported(model)?"試験的な論理波形近似。符号区間を矩形化／実機のアナログ回路は未再現":"未補正：出力回路の裏付けが不足しているため元波形を使用";
    }
    public static string ReadLog(string path) {
        string model=None,version=null;
        foreach(string line in File.ReadAllLines(path)) {
            if(line.StartsWith("波形補正機種: ")) model=Validate(line.Substring("波形補正機種: ".Length));
            if(line.StartsWith("波形補正方式: ")) version=line.Substring("波形補正方式: ".Length);
        }
        if(model!=None && version!=Version) throw new InvalidDataException("波形補正方式が一致しないため再現できません。");
        return model;
    }
    // Preserve sampled sign transitions, zero samples, duration and channel independence.
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
