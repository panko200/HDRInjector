# プラグイン情報

[![Release](https://img.shields.io/github/v/release/panko200/HDRInjector)](https://github.com/panko200/HDRInjector)
[![Downloads](https://img.shields.io/github/downloads/panko200/HDRInjector/total)](https://github.com/panko200/HDRInjector/releases/latest)
[![License](https://img.shields.io/github/license/panko200/HDRInjector)](https://github.com/panko200/HDRInjector/blob/master/LICENSE)
[![Last Commit](https://img.shields.io/github/last-commit/panko200/HDRInjector)](https://github.com/panko200/HDRInjector/commits/master)

HDRInjector  
製作者：Panko200  
配布場所：https://github.com/panko200/HDRInjector

## 概要

YukkuriMovieMaker4 にて HDR を使えるようにするプラグインです。HDR動画の読み込み、書き出し、映像エフェクトの追加などがなされます。  
HDRの動画読み込みなどは、GPUで処理されるため、高速に動作します。

## 使用方法

1. プラグインをインストールして YMM4 を起動する。
2. HDR動画をタイムラインへ追加する。
3. 通常の動画と同様に編集する。

## アンインストール方法

1. YMM4 を起動して `ヘルプ(H)` > `その他` > `プラグインフォルダを開く` をクリックする。
2. YMM4 を終了する。
3. `HDRInjector` という名前のフォルダを削除する。

## 注意点

OS : Windows 11 (64bit)  
ゆっくりMovieMaker4 : v4.55.1.0  
CPU : Ryzen 7 9700X  
GPU : NVIDIA Geforce RTX 4070 Ti  
RAM : DDR5 64GB  
にて動作確認をしています。

### 対応しているHDR動画

現在、以下のHDR形式について実機で動作確認しています。

- VP9 / HLG / 10bit / 4:2:0
- HEVC(H.265) / PQ(ST 2084) / 10bit / 4:2:0

AV1 HDR は動画自体の読み込みまで確認できていますが、手元の検証素材ではHDR色情報が正しく取得できないケースがあったため、現時点では正式なHDR対応として保証していません。

12bit HDR、HDR10+ などの動的メタデータを使用するHDR形式についても、現時点では動作保証していません。

### 運用上の注意

HDR動画であっても、動画ファイル側に適切なHDR色情報が記録されていない場合、HDR動画として認識されないことがあります。

また、動画形式・コーデック・色差サブサンプリング・ビット深度などによっては正常に扱えない場合があります。

_免責事項: 作者は、本プラグインの使用または使用不能に起因するいかなる損害についても、一切の責任を負いません。_

## アップデート内容

v0.1.0  
まともに動くようになった

v0.1.1  
ffprobe修正

v0.1.2  
ffprobe探索修正

v0.1.3  
Radeon対応(Arcは対応しておりません。)

v1.0.0  
初公開

## ライセンス

本プロジェクトは MIT License のもと公開しています。

[MIT License](./LICENSE)

### FFmpeg

本プラグインは、YukkuriMovieMaker4 に付属する FFmpeg ライブラリを利用しています。

FFmpeg は GNU Lesser General Public License Version 3 (LGPL v3) のもとでライセンスされています。

本プラグインでは FFmpeg のライブラリ自体を同梱・再配布していません。

[FFmpeg License](./FFmpeg.txt)

### FFmpeg.AutoGen

本プラグインでは FFmpeg.AutoGen を使用しています。

- License: MIT License
- Copyright: Ruslan Balanukhin (Rationale One)

[FFmpeg.AutoGen License](./FFmpeg.AutoGen.txt)

### Harmony

本プラグインでは、 Harmony を使用しています。

Harmony (Lib.Harmony)

- License: MIT License
- https://github.com/pardeike/Harmony

[Harmony License](./Harmony_LICENSE)

### ColorPicker_plus

本プラグインは、以下のYMM4プラグインを参考に制作されました。プラグインの開発者に感謝申し上げます。

**ColorPicker_plus**

- License: MIT License
- Author: leftcontroller0518
- URL: [https://github.com/leftcontroller0518/colorpicker_plus](https://github.com/leftcontroller0518/colorpicker_plus)
- [MIT License](./ColorPicker_plus_LICENSE)
