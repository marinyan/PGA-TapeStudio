# 波形補正の調査と実装範囲

目的は実機へのロード成功率を改善すること。SAVE信号への近似はその手段であり、波形の見た目が近いだけで成功とは判定しない。2026-10-12時点で実機ロードの改善は未検証。

## 実装済み：logic-v1

MZ_700、X1、FM_7、PASOPIA7、PYUTA、FP_1100、BML3について、以下のエミュレーター実装で確認できる二値の出力を参考に、試験的な論理波形近似を提供する。**各機種のアナログ出力回路を再現したものではない。7機種とも同じ処理を使う。** エミュレーターの実装はそのエミュレーターについての一次資料であり、実機の電圧・周波数特性の実測証拠ではない。

48 kHz / 16 bit / stereoに変換したPCMの、同じ符号が続く区間を、その区間の最大絶対振幅で矩形化する。左右は独立。ゼロのサンプル、サンプル数、符号が変わるサンプル位置を保持する。10 msを超える同符号区間はDCや長い休止の可能性があるため変更しない。この10 msは実機の回路定数ではなく処理対象を制限する設計値。

この方式では、低振幅ノイズをファイル全体の最大振幅まで引き上げない。ただし、ノイズ除去・欠落修復・DC除去は行わない。サンプル間の補間ゼロ交差や、DAC・アンプ通過後のエッジ位置まで同じとは限らない。元から矩形に近い音源では変化が少ない。実録音の丸い波形に適用すると悪化する可能性もある。

補正 → ゲイン（手動または全体ピークから自動）→ 位相反転の順。元ファイルには書き込まない。無編集はこの波形補正をスキップする意味で、既存のPCM変換・ゲイン・位相反転は独立して適用される。

## 機種別の根拠と保留事項

| 選択機種 | 調査で確認できた生成方法・参考実装 | 今回の扱い |
|---|---|---|
| PC_6001 / PC_6601SR | [MAME pc6001.cpp](https://github.com/mamedev/mame/blob/master/src/mame/nec/pc6001.cpp) のカセットコマンド処理に600/1200 baud選択。[PC6001VX tape.cpp](https://github.com/eighttails/PC6001VX/blob/master/src/tape.cpp) も調査。SAVE端子のアナログ特性やSR差を確定できていない | 未補正 |
| PC_8001 / PC_8001mkII / PC_8001mkIISR | [MAME pc8001.cpp](https://github.com/mamedev/mame/blob/master/src/mame/nec/pc8001.cpp) の8251・カセットFSK処理。世代別SAVE出力回路の定数は未確定 | 未補正 |
| PC_8801 / PC_8801mkIISR | [MAME pc8801.cpp](https://github.com/mamedev/mame/blob/master/src/mame/nec/pc8801.cpp) の8251処理。カセット送信処理に未実装部分があり、SAVE端子の再現根拠にできない | 未補正 |
| MZ_80K / MZ_80B | [MAME mz80.cpp](https://github.com/mamedev/mame/blob/master/src/mame/sharp/mz80.cpp)。MZ-700と共通のアナログ回路とは確認できていない | 未補正 |
| MZ_700 | [MAME mz700_m.cpp](https://github.com/mamedev/mame/blob/master/src/mame/sharp/mz700_m.cpp) の `pio_port_c_w`。8255 port C bit 1がWDATAとして二値出力される | 論理波形近似 |
| MZ_1500 | MZ-700互換性だけを根拠に出力回路も同一とは判断しない。専用の回路資料は未確認 | 未補正 |
| X1 | [MAME x1.cpp](https://github.com/mamedev/mame/blob/master/src/mame/sharp/x1.cpp) のPPI port C bit 0によるカセット出力。アナログ段・各X1モデルの差は未再現 | 論理波形近似 |
| MSX | [MSX2 Technical Handbook Chapter 5](https://github.com/Konamiman/MSX2-Technical-Handbook/blob/master/md/Chapter5a.md) に1200 bpsで1200/2400 Hz、2400 bpsで2400/4800 Hzをソフトウェアで生成する説明。出力フィルターまで検証したプロファイルは未完成 | 未補正 |
| PYUTA | [MAME tutor.cpp](https://github.com/mamedev/mame/blob/master/src/mame/tomy/tutor.cpp) の `tutor_cassette_w`。アドレスのbit 5で二値出力。同ドライバー自身にもテープデータに関するTODOがある | 論理波形近似・特に実機検証が必要 |
| FM_7 | [MAME fm7.cpp](https://github.com/mamedev/mame/blob/master/src/mame/fujitsu/fm7.cpp) のカセット／プリンター制御でbit 0が出力、bit 1がモーター | 論理波形近似 |
| PASOPIA7 | [MAME pasopia7.cpp](https://github.com/mamedev/mame/blob/master/src/mame/toshiba/pasopia7.cpp) の `nmi_mask_w` でbit 4が二値出力。そこでの論理極性をWAVへ重ねて反転することはしない | 論理波形近似 |
| FP_1100 | [MAME fp1100.cpp](https://github.com/mamedev/mame/blob/master/src/mame/casio/fp1100.cpp) のサブCPUシリアル出力と `kansas_w`。1200/2400 Hzの二値出力。ドライバー自身に実際の回路・読み込みに関する未解決事項あり | 論理波形近似・アナログ段未再現 |
| BML3 | [MAME bml3.cpp](https://github.com/mamedev/mame/blob/master/src/mame/hitachi/bml3.cpp) の `kansas_w` で1200/2400 Hzの二値出力 | 論理波形近似 |
| BML3_IR | 上記資料から、この認識形式専用のSAVE回路やBML3との差異は確定できない | 未補正 |

標準BASICのビット周波数に強制変換すると独自ローダーを壊しうるため、周波数・パルス幅の標準値への丸め、データ再符号化は行わない。絶対出力電圧とdBFSの対応はPC・デバイス・Windows音量・ケーブル依存なので自動換算しない。

## 確認方法

まず「無編集」でロードを確認し、同じ接続・音量で該当機種の補正を比較する。「再生＋録音測定」で認識機種も指定すると、既存のDumpListEditor検査で元音源・補正後の送信PCM・録音の復調データを比較できる。補正機種と認識機種は別設定。認識エンジンで一致しても実機成功の保証ではない。矩形化による平坦部をクリッピング判定が検出する場合もあるため、平坦部の数だけで失敗とは判断しない。

次の段階のアナログ回路近似には、機種／基板世代が明確なサービス回路図の確認、部品値と負荷を使った伝達特性の計算、実機SAVE録音との比較が必要。未確認値を機種ごとのEQとして割り当ててはいない。

## 書き出しと再現

「補正WAV保存」は全体を100%で、PCM 48 kHz / 16 bit / stereoの別ファイルへ書き出す。測定速度・測定秒数・モニター音量・Windowsデバイス音量は含めない。適用ゲイン・位相・補正機種・方式バージョンを隣の `.wav.txt` に保存する。書き出したWAVは既に補正済みなので、再読込時は原則「無編集」、位相反転OFF、手動ゲイン0 dBで使用する。

録音ログにも機種と方式を保存する。古い録音ログは無編集として再現し、未知の方式バージョンは再現を拒否する。書き出しだけでは認識・実機ロードは検査しない。
