using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Serialization;

namespace RecordingLevelChecker {
public sealed class RecognitionJob {
    public string EnginePath, WavePath, Model;
    public double FrequencyPercent=100;
}
public sealed class RecognitionResult {
    public string Error="", Details="", EngineHash="";
    public int Files, ByteErrors=-1, BitErrors=-1;
    public byte[] Bytes=new byte[0];
}
public static class TapeRecognition {
    public static readonly string[] Models={"PC_6001","PC_6601SR","PC_8001","PC_8001mkII","PC_8001mkIISR","PC_8801","PC_8801mkIISR","MZ_80K","MZ_80B","MZ_700","MZ_1500","X1","MSX","PYUTA","FM_7","PASOPIA7","FP_1100","BML3","BML3_IR"};
    public static void ReadRecordedSettings(string path,ref double speed,ref double gain,ref bool legacyKeepPitch,ref int seconds) {
        foreach(string line in File.ReadLines(path)) {
            var match=Regex.Match(line,@"^速度: ([0-9.]+) 倍$"); if(match.Success) speed=double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            match=Regex.Match(line,@"^出力ゲイン: (-?[0-9.]+) dB$"); if(match.Success) gain=double.Parse(match.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
            if(line=="音程維持: True") legacyKeepPitch=true;
            if(line=="音程維持: False") legacyKeepPitch=false;
            if(line=="測定上限: 全部") seconds=0;
            match=Regex.Match(line,@"^測定上限: (\d+) 秒$"); if(match.Success) seconds=int.Parse(match.Groups[1].Value);
        }
        if(speed<0.5 || speed>4 || gain<-60 || gain>0 || seconds<0 || seconds>86400) throw new InvalidDataException("対応範囲外の測定条件です。");
    }
    public static void SaveXml<T>(string path,T value) { using(var s=File.Create(path)) new XmlSerializer(typeof(T)).Serialize(s,value); }
    public static T LoadXml<T>(string path) { using(var s=File.OpenRead(path)) return (T)new XmlSerializer(typeof(T)).Deserialize(s); }
    // This adapter uses the user's installed DLE; no DLE binary or source is redistributed.
    public static int Worker(string jobPath,string resultPath) {
        var result=new RecognitionResult(); object wav=null; Type type=null;
        try {
            var job=LoadXml<RecognitionJob>(jobPath);
            using(var sha=SHA256.Create()) using(var s=File.OpenRead(job.EnginePath)) result.EngineHash=BitConverter.ToString(sha.ComputeHash(s)).Replace("-","");
            var assembly=Assembly.LoadFrom(job.EnginePath); type=assembly.GetType("DumpListEditor.WavFile",true);
            var model=Enum.Parse(assembly.GetType("DumpListEditor.MODEL",true),job.Model);
            wav=Activator.CreateInstance(type,new object[]{null,File.ReadAllBytes(job.WavePath),job.WavePath});
            Action<string,object[]> call=(name,values)=>type.GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(wav,values);
            call("SetModel",new object[]{model,job.FrequencyPercent.ToString("F0",System.Globalization.CultureInfo.InvariantCulture),"100",false,false});
            call("SetBuadRate",new object[]{Enum.Parse(assembly.GetType("DumpListEditor.WavFile+BAUD_RATE"),"AUTO")});
            call("SetEdge",new object[]{4});
            call("SetDataFilter",new object[]{Enum.Parse(assembly.GetType("DumpListEditor.WavFile+FILTER"),"AUTO")});
            call("SetCheckLevel",new object[]{3});
            call("SetFtAnalyze",new object[]{false,"0"});
            call("SetLogFg",new object[]{false});
            var arguments=new object[]{"","",false,0.1,0.1,false}; call("GetT88Data",arguments);
            result.Details=arguments[0]+"\r\n"+arguments[1];
            result.Files=(int)type.GetMethod("GetNumFile",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Invoke(wav,new object[]{false});
            var raw=(int[])type.GetMethod("GetByteDataInt").Invoke(wav,null);
            result.Bytes=raw.Where(b=>b>=0 && b<=255).Select(b=>(byte)b).ToArray();
            var byteError=Regex.Match(result.Details,@"Byte Error\s*=\s*(\d+)");
            var bitError=Regex.Match(result.Details,@"Bit Error\s*=\s*(\d+)");
            if(byteError.Success) result.ByteErrors=int.Parse(byteError.Groups[1].Value);
            if(bitError.Success) result.BitErrors=int.Parse(bitError.Groups[1].Value);
        } catch(Exception ex) { result.Error=ex.GetBaseException().Message; }
        finally { if(wav!=null) { try { type.GetMethod("Dispose").Invoke(wav,null); } catch { } } }
        SaveXml(resultPath,result); return result.Error.Length==0?0:1;
    }
    public static RecognitionResult RunEngine(string engine,string model,string wave,double frequency,string directory,CancellationToken token) {
        string id=Guid.NewGuid().ToString("N"), job=Path.Combine(directory,id+".job.xml"), output=Path.Combine(directory,id+".result.xml");
        SaveXml(job,new RecognitionJob { EnginePath=Path.GetFullPath(engine), Model=model, WavePath=wave, FrequencyPercent=frequency });
        var info=new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"RecordingLevelChecker.exe"),"--recognize \""+job+"\" \""+output+"\"") { UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden, WorkingDirectory=directory };
        using(var process=Process.Start(info)) {
            var clock=Stopwatch.StartNew();
            while(!process.WaitForExit(100)) {
                if(token.IsCancellationRequested || clock.Elapsed.TotalMinutes>5) {
                    process.Kill(); process.WaitForExit(); token.ThrowIfCancellationRequested();
                    return new RecognitionResult { Error="認識処理が5分を超えたため中止しました。" };
                }
            }
            token.ThrowIfCancellationRequested();
            if(!File.Exists(output)) return new RecognitionResult { Error="DumpListEditorの認識処理が結果を返さず終了しました。" };
            return LoadXml<RecognitionResult>(output);
        }
    }
    public static string CompareBytes(string name,RecognitionResult expected,RecognitionResult actual) {
        if(expected.Error.Length>0 || actual.Error.Length>0) return name+"：判定不能（認識処理のエラー）";
        if(expected.Bytes.Length==0 || actual.Bytes.Length==0) return name+"：一致を確認できません（復調データなし）";
        int first=0,limit=Math.Min(expected.Bytes.Length,actual.Bytes.Length);
        while(first<limit && expected.Bytes[first]==actual.Bytes[first]) first++;
        if(first==limit && expected.Bytes.Length==actual.Bytes.Length) {
            bool strict=expected.Files>0 && actual.Files>0 && expected.ByteErrors==0 && actual.ByteErrors==0 && expected.BitErrors==0 && actual.BitErrors==0;
            return name+"：復調バイト列は完全一致（"+limit+" bytes）"+(strict?"。ファイル認識あり・報告されたビット／バイトエラーなし。":"。ただし認識ファイル数またはエラー情報に要確認項目あり。");
        }
        return string.Format("{0}：不一致。期待 {1} / 実際 {2} bytes、最初の差は0x{3:X}（{3}番目、0起点）。",name,expected.Bytes.Length,actual.Bytes.Length,first);
    }
    public static string Check(string source,byte[] playback,short[] recording,string engine,string model,double speed,bool keepPitch,int channel,bool whole,CancellationToken token) {
        if(!File.Exists(engine)) throw new FileNotFoundException("DumpListEditor.exeを選択してください。",engine);
        string folder=Path.Combine(Path.GetTempPath(),"level-recognition-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try {
            string originalPath=Path.Combine(folder,"original.wav"), playbackPath=Path.Combine(folder,"playback.wav"), recordedPath=Path.Combine(folder,"recorded.wav");
            byte[] original=Decoder.Decode(source,1,0,0,false,token);
            AudioEngine.SaveWave(originalPath,WaveView.ExtractPlayback(original,channel)); original=null;
            AudioEngine.SaveWave(playbackPath,WaveView.ExtractPlayback(playback,channel));
            AudioEngine.SaveWave(recordedPath,recording);
            var a=RunEngine(engine,model,originalPath,100,folder,token);
            var b=RunEngine(engine,model,playbackPath,keepPitch?100:speed*100,folder,token);
            var c=RunEngine(engine,model,recordedPath,keepPitch?100:speed*100,folder,token);
            var report=new StringBuilder("テープデータ認識検査 / DumpListEditor\r\n機種: "+model+" / 自動補完OFF・厳格チェック\r\n");
            if(!whole) report.AppendLine("部分測定：元音源全体との長さ不一致は想定されます。全体の完全性は判定できません。");
            report.AppendLine(CompareBytes("元音源 → 再生データ",a,b)); report.AppendLine(CompareBytes("再生データ → 録音",b,c));
            report.AppendLine(CompareBytes("元音源 → 録音",a,c));
            if(keepPitch && speed!=1) report.AppendLine("この旧録音は音程維持で速度変換されています。テープ符号が変わる場合があるため、現在の周波数も変わる再生方式で再測定してください。");
            string[] names={"元音源","再生データ","録音"}; var results=new[]{a,b,c};
            for(int i=0;i<3;i++) {
                var r=results[i]; report.AppendFormat("\r\n[{0}] 認識ファイル {1} / 復調 {2} bytes / Bit Error {3} / Byte Error {4}\r\n",names[i],r.Files,r.Bytes.Length,r.BitErrors<0?"不明":r.BitErrors.ToString(),r.ByteErrors<0?"不明":r.ByteErrors.ToString());
                report.AppendLine(r.Error.Length>0?r.Error:r.Details);
            }
            report.AppendLine("エンジンSHA256: "+a.EngineHash);
            report.AppendLine("一致は選択機種で復調できたバイト列の比較です。独自ローダーの意味や実機での起動成功、未復調領域まで保証するものではありません。DumpListEditorの内部APIはバージョンによって変わる場合があります。");
            return report.ToString();
        } finally { foreach(string path in Directory.GetFiles(folder)) File.Delete(path); Directory.Delete(folder); }
    }
}
}
