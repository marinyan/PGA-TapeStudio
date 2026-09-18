using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace RecordingLevelChecker {
public sealed class SimilarityWindow {
    public double StartSeconds, EndSeconds, Similarity;
}
public sealed class ComparisonResult {
    public bool Aligned;
    public double Correlation, DelayMs, Coverage, WorstResidual, Gain;
    public int ActiveWindows, SuspiciousWindows;
    public string Channel, Summary;
    public double MeanSimilarity, MinimumSimilarity = 1;
    public int LowSimilarityWindows;
    public readonly List<SimilarityWindow> SimilarityWindows = new List<SimilarityWindow>();
    public readonly List<string> Issues = new List<string>();
    public string Report() {
        var s=new StringBuilder(); s.AppendLine(Summary);
        if(SimilarityWindows.Count>0)
            s.AppendFormat("波形相似度：平均 {0:F2}% / 最低 {1:F2}% / {2:F0}%未満 {3} 区間\r\n",MeanSimilarity*100,MinimumSimilarity*100,SignalComparison.SimilarityThreshold*100,LowSimilarityWindows);
        if(Aligned) {
            s.AppendFormat("比較対象: {0} / 遅延: {1:F2} ms / 極性: {2}\r\n",Channel,DelayMs,Gain<0?"反転":"同相");
            s.AppendFormat("基準区間の相関: {0:F3} / 録音倍率: {1:F3} / 比較範囲: {2:F1}%\r\n",Correlation,Gain,Coverage*100);
            s.AppendFormat("有音区間: {0} / 要確認区間: {1} / 最大相対誤差: {2:F1}%\r\n",ActiveWindows,SuspiciousWindows,WorstResidual*100);
        }
        foreach(string issue in Issues) s.AppendLine(issue);
        if(SimilarityWindows.Count>0) {
            s.AppendLine("区間別の波形相似度（遅延・音量・DCオフセット・全体の極性を補正）:");
            foreach(var w in SimilarityWindows)
                s.AppendFormat("  {0:F3}–{1:F3} 秒 : {2:F2}%{3}\r\n",w.StartSeconds,w.EndSeconds,w.Similarity*100,w.Similarity<SignalComparison.SimilarityThreshold?"  要確認":"");
            s.AppendLine("相似度は相関に基づく指標で、正常である確率ではありません。無音区間は集計対象外です。");
        }
        s.Append("時刻は速度変換後の再生音声に対する値です。アナログ経路の周波数特性・雑音・補正も差に含まれます。破損の有無を保証する判定ではありません。");
        return s.ToString();
    }
}
public static class SignalComparison {
    public const double SimilarityThreshold = 0.95;
    static float[] Smooth(float[] source) {
        var filtered=new float[source.Length]; double sum=0;
        for(int i=0;i<source.Length;i++) { sum+=source[i]; if(i>=48) sum-=source[i-48]; filtered[i]=(float)(sum/48); }
        return filtered;
    }
    static short[] Smooth(short[] source) {
        var filtered=new short[source.Length]; long sum=0;
        for(int i=0;i<source.Length;i++) { sum+=source[i]; if(i>=48) sum-=source[i-48]; filtered[i]=(short)(sum/48); }
        return filtered;
    }
    struct Fit { public double Correlation, Gain, Offset, RefRms, RecordedRms; public int Count; }
    static Fit Measure(float[] reference,short[] recording,int start,int count,int lag,int stride) {
        double sx=0,sy=0,sxx=0,syy=0,sxy=0; int n=0;
        int end=Math.Min(reference.Length,start+count);
        for(int i=Math.Max(start,-lag);i<end && i+lag<recording.Length;i+=stride) {
            double x=reference[i], y=recording[i+lag]/32768.0;
            sx+=x; sy+=y; sxx+=x*x; syy+=y*y; sxy+=x*y; n++;
        }
        if(n<4) return new Fit();
        double xx=Math.Max(0,sxx-sx*sx/n), yy=Math.Max(0,syy-sy*sy/n), xy=sxy-sx*sy/n;
        double gain=xx>1e-15?xy/xx:0;
        return new Fit { Count=n, Correlation=xx*yy>1e-20 ? Math.Max(-1,Math.Min(1,xy/Math.Sqrt(xx*yy))):0, Gain=gain, Offset=(sy-gain*sx)/n, RefRms=Math.Sqrt(xx/n), RecordedRms=Math.Sqrt(yy/n) };
    }
    static int BestLag(float[] reference,short[] recording,int start,int count,int from,int to,int step,int stride,CancellationToken token,out Fit best) {
        best=new Fit(); int lagBest=from; double score=-1;
        for(int lag=from;lag<=to;lag+=step) {
            token.ThrowIfCancellationRequested();
            var fit=Measure(reference,recording,start,count,lag,stride);
            if(fit.Count<count/stride*0.9) continue;
            double value=Math.Abs(fit.Correlation);
            if(value>score) { best=fit; score=value; lagBest=lag; }
        }
        return lagBest;
    }
    public static ComparisonResult Compare(byte[] pcm,short[] recording,CancellationToken token) {
        int frames=pcm.Length/4;
        var references=new float[][] { new float[frames],new float[frames],new float[frames] };
        int sourceRail=0;
        for(int i=0;i<frames;i++) {
            short l=(short)(pcm[i*4]|pcm[i*4+1]<<8), r=(short)(pcm[i*4+2]|pcm[i*4+3]<<8);
            references[0][i]=l/32768f; references[1][i]=r/32768f; references[2][i]=(l+r)/65536f;
            if(Math.Abs((int)l)>=32760 || Math.Abs((int)r)>=32760) sourceRail++;
        }
        var result=new ComparisonResult();
        if(frames<4800 || recording.Length<4800) { result.Summary="比較不能：音声が短すぎます。"; return result; }
        // Choose a high-energy anchor for each possible mono wiring; quiet intros do not anchor alignment.
        double bestScore=-1; int selected=0, offset=0; Fit global=new Fit();
        var smoothRecording=Smooth(recording);
        int anchorCount=Math.Min(48000,frames);
        for(int channel=0;channel<3;channel++) {
            var reference=references[channel]; int anchor=0; double energy=-1;
            for(int p=0;p<=frames-anchorCount;p+=12000) {
                var fit=Measure(reference,recording,p,anchorCount,0,32);
                if(fit.RefRms>energy) { energy=fit.RefRms; anchor=p; }
            }
            if(energy<0.001) continue;
            Fit coarse,fine;
            var smoothedReference=Smooth(reference);
            int candidate=BestLag(smoothedReference,smoothRecording,anchor,anchorCount,0,96000,48,16,token,out coarse);
            candidate=BestLag(reference,recording,anchor,anchorCount,Math.Max(0,candidate-96),candidate+96,1,4,token,out fine);
            // A damaged loudest segment must not prevent using an intact segment for alignment.
            if(Math.Abs(fine.Correlation)<0.6) {
                foreach(int alternate in new int[] { 0, frames-anchorCount }) {
                    Fit alternative;
                    int alternativeLag=BestLag(smoothedReference,smoothRecording,alternate,anchorCount,0,96000,48,16,token,out coarse);
                    alternativeLag=BestLag(reference,recording,alternate,anchorCount,Math.Max(0,alternativeLag-96),alternativeLag+96,1,4,token,out alternative);
                    if(Math.Abs(alternative.Correlation)>Math.Abs(fine.Correlation) && alternative.RefRms>=0.001) { candidate=alternativeLag; fine=alternative; }
                }
            }
            if(Math.Abs(fine.Correlation)>bestScore) { bestScore=Math.Abs(fine.Correlation); global=fine; selected=channel; offset=candidate; }
        }
        if(bestScore<0.6 || global.RecordedRms<0.0001) {
            result.Summary="比較不能：再生音声と録音の対応を十分に確認できません。";
            result.Issues.Add("接続・入力先・録音レベルを確認してください。大きな歪みや2秒を超える遅延でも比較できません。");
            return result;
        }
        result.Aligned=true; result.Channel=new string[] { "左チャンネル", "右チャンネル", "左右の平均" }[selected];
        result.Correlation=bestScore; result.Gain=global.Gain; result.DelayMs=offset/48.0;
        result.Coverage=Math.Min(1,Math.Max(0,recording.Length-offset)/(double)frames);
        var chosen=references[selected]; var smoothChosen=Smooth(chosen); int currentLag=offset; int window=24000;
        double weightedSimilarity=0; long comparedSamples=0;
        for(int start=0;start<frames;start+=window) {
            token.ThrowIfCancellationRequested(); int length=Math.Min(window,frames-start);
            if(length<4) continue;
            Fit baseline=Measure(chosen,recording,start,length,currentLag,1);
            if(baseline.Count<length*0.9) { result.Issues.Add(string.Format("{0:F2} 秒～：録音が不足しています。",start/48000.0)); break; }
            if(baseline.RefRms<0.001) {
                if(baseline.RecordedRms>0.01) { result.SuspiciousWindows++; result.Issues.Add(string.Format("{0:F2}–{1:F2} 秒：元音声がほぼ無音の区間に入力音があります。",start/48000.0,(start+length)/48000.0)); }
                continue;
            }
            Fit coarse,fit;
            int lag=BestLag(smoothChosen,smoothRecording,start,length,Math.Max(0,currentLag-240),currentLag+240,8,16,token,out coarse);
            lag=BestLag(chosen,recording,start,length,Math.Max(0,lag-8),lag+8,1,8,token,out fit);
            // Decimation is for lag search only. Score every sample, including a short final window.
            fit=Measure(chosen,recording,start,length,lag,1);
            double similarity=Math.Max(0,Math.Min(1,fit.Correlation*Math.Sign(global.Gain)));
            result.SimilarityWindows.Add(new SimilarityWindow { StartSeconds=start/48000.0, EndSeconds=(start+length)/48000.0, Similarity=similarity });
            weightedSimilarity+=similarity*fit.Count; comparedSamples+=fit.Count;
            result.MinimumSimilarity=Math.Min(result.MinimumSimilarity,similarity);
            bool dissimilar=similarity<SimilarityThreshold;
            if(dissimilar) result.LowSimilarityWindows++;
            double correlation=Math.Abs(fit.Correlation);
            double residual=Math.Sqrt(Math.Max(0,1-correlation*correlation));
            double ratio=Math.Abs(fit.Gain/global.Gain);
            bool dropout=fit.RecordedRms < baseline.RefRms*Math.Abs(global.Gain)*0.2;
            bool timing=Math.Abs(lag-currentLag)>96;
            bool transient=false; int transientAt=start;
            // Inspect 10 ms blocks at full sample rate so isolated clicks/gaps cannot hide in a half-second average.
            for(int sub=start;sub<start+length;sub+=480) {
                double error=0,energy=0; int valid=0;
                for(int i=sub;i<Math.Min(sub+480,start+length);i++) {
                    int index=i+lag; if(index<0 || index>=recording.Length) continue;
                    double expected=chosen[i]*fit.Gain, difference=recording[index]/32768.0-expected-fit.Offset;
                    energy+=expected*expected; error+=difference*difference; valid++;
                }
                if(valid>0 && energy/valid>0.000001 && error>energy*0.25) { transient=true; transientAt=sub; break; }
            }
            bool changed=dissimilar || dropout || residual>0.35 || ratio<0.5 || ratio>2 || timing || transient || fit.Gain*global.Gain<0;
            result.ActiveWindows++; result.WorstResidual=Math.Max(result.WorstResidual,residual);
            if(changed) {
                result.SuspiciousWindows++;
                if(dissimilar) result.Issues.Add(string.Format("{0:F3}–{1:F3} 秒：波形相似度 {2:F2}%（基準 {3:F0}%未満）",start/48000.0,(start+length)/48000.0,similarity*100,SimilarityThreshold*100));
                if(result.Issues.Count<100) result.Issues.Add(string.Format("{0:F2}–{1:F2} 秒：{2}（相関 {3:F3}、相対誤差 {4:F1}%、音量比 {5:F2}）",start/48000.0,(start+length)/48000.0,dropout?"音抜け・大幅な減衰の疑い":timing?"時間ずれの変化":transient?string.Format("{0:F3} 秒付近に短時間の波形差",transientAt/48000.0):"波形・音量の差が大きい",correlation,residual*100,ratio));
            }
            if(correlation>=0.8 && !dropout) currentLag=lag;
        }
        result.MeanSimilarity=comparedSamples>0?weightedSimilarity/comparedSamples:0;
        if(sourceRail>0) result.Issues.Add("再生用データ自体にもフルスケール付近の値があります（"+sourceRail+" frames）。元音源の歪みと経路の歪みは、この測定だけでは区別できません。");
        result.Summary=result.Coverage<0.98?"要確認：再生範囲に対して録音が不足しています。":result.ActiveWindows==0?"比較不能：比較に使える有音区間がありません。":result.SuspiciousWindows>0?"要確認：再生データとの差が大きい区間があります。":"比較完了：検出条件を超える波形差・音抜けは見つかりませんでした。";
        return result;
    }
}
}
