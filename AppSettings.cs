using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace RecordingLevelChecker {
public sealed class AppSettings {
    public string InputName="", OutputName="", LastFile="", MonitorName="";
    public bool MonitorEnabled;
    public bool AutoGain;
    public int MonitorVolumePercent=100;
    public string RecognitionModel="", RecognitionEngine="";
    public decimal Speed=2, Gain=-18, Seconds=15, Zoom=50;
    public bool WholeFile=true, AlignWaves=true;
    public int WaveChannel;
    public void Normalize() {
        Speed=Math.Max(0.5m,Math.Min(4,Speed)); Gain=Math.Max(-60,Math.Min(0,Gain));
        Seconds=Math.Max(1,Math.Min(86400,Seconds)); Zoom=Math.Max(1,Math.Min(2000,Zoom));
        WaveChannel=Math.Max(0,Math.Min(2,WaveChannel));
        MonitorVolumePercent=Math.Max(0,Math.Min(100,MonitorVolumePercent));
    }
    public static AppSettings Load(string path) {
        if(!File.Exists(path)) return new AppSettings();
        using(var reader=XmlReader.Create(path,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit, XmlResolver=null })) {
            var settings=(AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(reader);
            settings.Normalize(); return settings;
        }
    }
    public void Save(string path) {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var writer=File.Create(temp)) new XmlSerializer(typeof(AppSettings)).Serialize(writer,this);
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        } finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public static int FindDevice(string[] names,string saved) {
        if(string.IsNullOrEmpty(saved)) return names.Length>0?0:-1;
        // WinMM truncates device names: ambiguous or disconnected saved devices require reselection.
        int match=-1;
        for(int i=0;i<names.Length;i++) if(names[i]==saved) { if(match>=0) return -1; match=i; }
        return match;
    }
    public static string DroppedFile(string[] paths) {
        return paths!=null && paths.Length==1 && File.Exists(paths[0])?paths[0]:null;
    }
}
}
