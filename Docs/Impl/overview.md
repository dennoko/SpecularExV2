# SpecularExV2 要件仕様書・設計概要 (Overview & Requirements)

本書は、lilToon 2.x 向け拡張シェーダー **SpecularExV2**（旧バージョン: v1 `dennoko_specularex`）の機能要件、技術仕様、およびアーキテクチャ設計を定義したドキュメントです。

---

## 1. 背景と開発目的

### 1.1 v1 の実績と課題
v1（[v1 仕様・リファレンス](ref/v1.md)）では、2層スペキュラーや独立 MatCap などを導入し、高い質感を表現可能としました。  
しかし、カットアウト (Cutout) や半透明 (Transparent) 描画モードにおいて、VRChat アップロード（Build & Publish）時に以下の問題が発生しました。

- **現象**: アップロード後、シーン上やVRChat内でアバターのマテリアルが透明化（不可視）になり、描画が消失する。
- **根本原因**: lilToon のビルド後処理（全機能有効化での再コンパイル）において、シェーダーで宣言される **テクスチャパラメータ数が GPU 上限（64枚）を超過（65枚に到達）** してシェーダーのコンパイル/ロードに失敗していた。
- **サンプラー制限との違い**: Direct3D 11 / ps_4_0 の「16サンプラー制限」は共有サンプラー（`sampler_linear_repeat`）で回避できていたが、それとは独立した「テクスチャパラメータ数 64」のハードウェア/API上限に抵触していた。

### 1.2 SpecularExV2 の目的
`Assets/dennokoworks/DennokoEx` で確立された **単一チャンネルマスクの自動 RGBA パッキング機構** を全面的に導入し、64テクスチャパラメータ上限を確実に回避しながら、以下の4つの拡張機能を安定して提供します。

1. **追加スペキュラー (Specular 2nd)**: lilToon リアルモード準拠の反射ロジック＋ForwardAdd（追加ライト）完全対応
2. **ワールド固定 MatCap (World-Oriented Dual-Hemisphere Matcap)**: 視点回転に追従せずワールド空間の反射を表現するデュアル半球MatCap（疑似Cubemap）
3. **追加ノーマル (Normal Map 3rd)**: 標準スロットを上書きせず高品質に加算合成する第3の法線マップ
4. **追加リムライト (Rim Light 2nd)**: 光源方向依存を排除した軽量設計＋4種のブレンドモード対応
5. **追加リムライト (Rim Light 3rd)**: Rim Light 2nd と同等の完全な第3のリムライト層

---

## 2. コア機能要件

### 2.1 スペキュラー 2nd (Specular 2nd)
lilToon 本体の「反射（リアルモード）」と同等の GGX / Blinn-Phong ハイライトロジックを持ち、追加のシャープな反射光や金属質感を付与します。

- **反射計算ロジック**:
  - 法線 $N$、視線ベクトル $V$、光源方向 $L$ からハーフベクトル $H = \mathrm{normalize}(V + L)$ を算出。
  - スムースネスからラフネス $\alpha = \mathrm{roughness}^2$ を求め、GGX / Blinn-Phong スペキュラー項を計算。
  - $N \cdot L$ による陰影減衰および影マスク（`shadowmix`）を適用。
- **ForwardAdd パス対応**:
  - ForwardBase（メインディレクショナルライト / 環境光）だけでなく、**ForwardAdd（ポイントライト・スポットライトなどの追加光源）** でも同一のスペキュラー計算を実行。
  - 追加ライトの光色 `fd.lightColor` と距離減衰 `fd.attenuation` を乗算して加算合成。
  - 有効化フラグとして本体同様の「ForwardAddで適用 (`_CustomRefl2ndApplyFA`)」トグルを提供。
- **パラメーター一覧**:
  - 有効化トグル (`_CustomRefl2ndEnabled` / `_CustomRefl2ndUIEnabled`)
  - 反射カラー (`_CustomRefl2ndColor`, HDR)
  - 強度スライダー (`_CustomRefl2ndStrength`)
  - スムースネス (`_CustomRefl2ndSmoothness`)
  - メタリック / 反射率 (`_CustomRefl2ndMetallic` / `_CustomRefl2ndReflectance`)
  - 法線影響度 (`_CustomRefl2ndNormalStrength`): 0で幾何法線 `origN`、1で法線マップ後 `fd.N`
  - 影減衰度 (`_CustomRefl2ndShadowAttenuation`)
  - メインカラー乗算度 (`_CustomRefl2ndMainColorStrength`)
  - 光源方向の補正 (`_CustomRefl2ndFakeLightBlend` / `_CustomRefl2ndFakeLightDir`): ForwardBase の光源方向をカメラ基準の方向 (x=右, y=上, z=視点側) へ寄せる。方向のみ変え、明るさはライティングに従う
  - ライティング反映 (`_CustomRefl2ndEnableLighting`, 既定 1): ライト色の乗算度。ForwardAdd では `lightColor × 値`
  - クリアコート (`_CustomRefl2ndClearCoat`): F0 = 0.04 固定 + 下地を視線フレネルで減衰 (後段の lilToon 反射等は減衰対象外)
  - フレネル強度 / 鋭さ (`_CustomRefl2ndFresnelStrength` / `_CustomRefl2ndFresnelPower`)
  - 適用マスク (`_CustomRefl2ndMaskTex`): ※エディタで自動パック（Pack 1 Rチャンネル）

---

### 2.2 スペキュラー 3rd (Specular 3rd)
Specular 2nd と同等の完全な第3のスペキュラー層です。独立したカラー、強度、スムースネス、金属質感、マスクを持ち、多層ハイライトや異素材の光沢表現を実現します。

- **パラメーター一覧**:
  - 有効化トグル (`_CustomRefl3rdEnabled` / `_CustomRefl3rdUIEnabled`)
  - 反射カラー (`_CustomRefl3rdColor`, HDR)
  - 強度スライダー (`_CustomRefl3rdStrength`)
  - スムースネス (`_CustomRefl3rdSmoothness`)
  - メタリック / 反射率 (`_CustomRefl3rdMetallic` / `_CustomRefl3rdReflectance`)
  - 法線影響度 (`_CustomRefl3rdNormalStrength`)
  - 影減衰度 (`_CustomRefl3rdShadowAttenuation`)
  - メインカラー乗算度 (`_CustomRefl3rdMainColorStrength`)
  - 光源方向の補正 (`_CustomRefl3rdFakeLightBlend` / `_CustomRefl3rdFakeLightDir`): ForwardBase の光源方向をカメラ基準の方向 (x=右, y=上, z=視点側) へ寄せる。方向のみ変え、明るさはライティングに従う
  - ライティング反映 (`_CustomRefl3rdEnableLighting`, 既定 1): ライト色の乗算度。ForwardAdd では `lightColor × 値`
  - クリアコート (`_CustomRefl3rdClearCoat`): F0 = 0.04 固定 + 下地を視線フレネルで減衰 (後段の lilToon 反射等は減衰対象外)
  - フレネル強度 / 鋭さ (`_CustomRefl3rdFresnelStrength` / `_CustomRefl3rdFresnelPower`)
  - 適用マスク (`_CustomRefl3rdMaskTex`): ※エディタで自動パック（Pack 2 Rチャンネル）

---

### 2.2 追加 Matcap (World-Oriented Dual-Hemisphere Matcap)
従来の MatCap はカメラビュー空間（View Space）に固定され、カメラが回転してもハイライトが画面に対して静止して見えます。  
本機能では、**「ワールド座標に固定された周囲空間を反射しているような表現」** を、Cubemap を使用せず 1枚の Matcap（後半球は鏡像）によって実現し、通常のビュー MatCap との間を任意の強さでブレンドできます。

- **空間表現の幾何ロジック**:
  - ワールド空間の反射ベクトル $R = \mathrm{reflect}(-V, N)$ を算出。
  - 通常の視線追従 MatCap（$N_{\mathrm{vs}}$ サンプリング）ではなく、**反射ベクトル $R$ のワールド空間座標** を基準にサンプリング UV を生成。
  - **球体空間の2半球（Dual-Hemisphere）**:
    - 全天球を「前半球（$+Z$ 側）」と「後半球（$-Z$ 側）」の2つに分割。
    - **テクスチャは1枚** (`_CustomMatcapFrontTex`)。後半球には前半球の鏡像を使う（$R_z$ を正側に折り返す）ので、$R_z = 0$ の境界でも連続になり、サンプルは1回で済む。
    - アバターが回転したり視点を動かした際、ワールド空間に固定された背景が滑らかに映り込む（軽量な疑似 Cubemap として動作）。
- **ワールド固定** (`_CustomMatcapWorldFixed`, 0〜1。互換のため名前は維持):
  - 0 = ビュー（通常の MatCap。参照方向はビュー空間法線 `mul(fd.cameraMatrix, N)`）
  - 1 = ワールド固定（参照方向はワールド反射ベクトル $R$）
  - 中間 = **参照方向のブレンド**: 両方の方向を $z \ge 0$ に折り返してから `normalize(lerp(dView, dWorld, t))` を取り、1回だけサンプリングする（`DNKW_MatcapUV`）。色をクロスフェードしないので中間値でもハイライトは1つで、カメラに遅れて追従する見え方になる。0 と 1 はそれぞれ純粋なモードと完全に一致する。
  - 旧マテリアルの値 2（廃止したオブジェクト固定）はシェーダー内の `saturate` で 1 として扱う。
- **回転オフセット**:
  - Y 軸周りの回転角度（Yaw Rotation）スライダーでワールド側の反射向きを調整する（ワールド固定 > 0 のときだけ表示）。
- **パラメーター一覧**:
  - 有効化トグル (`_CustomMatcapEnabled` / `_CustomMatcapUIEnabled`)
  - テクスチャ (`_CustomMatcapFrontTex`)
  - ※ 旧 `_CustomMatcapBackTex` は廃止（残っていても参照しない。後半球は前半球の鏡像になる）
  - ワールド固定 (`_CustomMatcapWorldFixed`, 0〜1) と Yaw 回転 (`_CustomMatcapWorldRotation`)
  - 合成カラー (`_CustomMatcapColor`, HDR)
  - 強度・不透明度 (`_CustomMatcapAlpha`)
  - ブレンドモード (`_CustomMatcapBlendMode`): 通常(0), 加算(1), スクリーン(2), 乗算(3)
  - ぼかし・LOD (`_CustomMatcapBlur`)
  - 法線影響度 (`_CustomMatcapNormalStrength`)
  - ライティング反映 (`_CustomMatcapEnableLighting`)
  - 影マスク反映 (`_CustomMatcapShadowStrength`)
  - ポリゴン裏面無効化 (`_CustomMatcapDisableBackface`)
  - HSVG 調整 (`_CustomMatcapHSVG`: 色相, 彩度, 明度, ガンマ。`lilToneCorrection`、既定値では処理を省略)
  - メインカラー乗算度 (`_CustomMatcapMainColorStrength`)
  - 適用マスク (`_CustomMatcapMaskTex`): ※エディタで自動パック（Aチャンネル）

---

### 2.3 追加ノーマルマップ (Normal Map 3rd)
lilToon 本体の Normal 1st（`_BumpMap`）および Normal 2nd（`_Bump2ndMap`）を上書き・破壊せず、その上にレイヤーとして加算合成（Compositing）します。

- **合成計算ロジック**:
  - タンジェント空間において、lilToon の標準関数 `lilUnpackNormalScale` および `lilBlendNormal`（Whiteout Blend 方式）を使用。
  - 既存の法線結果 `mul(fd.TBN, fd.N)` と追加ノーマル `_CustomNormal3rdTex` をブレンド。
  - 合成後の法線をワールド空間へ戻し、正規化: `fd.N = normalize(mul(nBlend, fd.TBN))`。
- **派生値のリフレッシュ**:
  - `fd.N` が更新された後、lilToon が法線から算出している以下の派生値を全て再計算（リフレッシュ）し、後続のライティングやシェーディングに正しく伝播させる。
    - `fd.reflectionN = fd.N`
    - `fd.matcapN = fd.N`
    - `fd.uvMat = mul(fd.cameraMatrix, fd.N).xy * 0.5 + 0.5`
    - `fd.ln = dot(fd.L, fd.N)`
    - `fd.nv = saturate(dot(fd.N, fd.V))`
    - `fd.uvRim = float2(fd.nvabs, fd.nvabs)`
- **パラメーター一覧**:
  - 有効化トグル (`_CustomNormal3rdEnabled` / `_CustomNormal3rdUIEnabled`)
  - ノーマルマップテクスチャ (`_CustomNormal3rdTex`, Tiling/Offset 対応)
  - ノーマル強度 (`_CustomNormal3rdStrength`, -2.0 〜 2.0)
  - UV 選択モード (`_CustomNormal3rdTex_UVMode`: UV0, UV1, UV2, UV3)
  - UV スクロール / 回転 (`_CustomNormal3rdTex_ScrollRotate`: lilToon の ScrollRotate 形式、`lilCalcUV`。マスクは動かない)
  - 距離フェード (`_CustomNormal3rdDistanceFade`: x=開始[m], y=終了[m], z=強度。頭からの距離 `fd.depth` で強度を下げる)
  - 適用マスク (`_CustomNormal3rdMaskTex`): ※エディタで自動パック（Bチャンネル）

---

### 2.4 追加リムライト (Rim Light 2nd)
lilToon 本体のリムライトに加えて独立して発光/陰影効果を付与できる第2のリムライトです。

- **計算ロジックの簡略化**:
  - 計算負荷軽減のため、**光源方向の影響度（`_RimLightDirection`）は 0 固定（非依存）** とし、純粋な視線フレネル項 $1.0 - \mathrm{saturate}(N \cdot V)$ のみで計算。
  - 法線はメッシュ幾何法線 `origN` と法線マップ後 `N` のブレンドが可能。
- **多彩なブレンドモード**:
  - 通常のリムライト（ハイライト発光）だけでなく、暗く落とし込むリムシェードにも対応できるよう、4つのブレンドモードをサポート。
    - **通常 (Replace / Lerp)**: ベースカラーをリムカラーで上書き補間
    - **加算 (Add)**: ベースカラーにリムカラーを加算（光沢・発光）
    - **スクリーン (Screen)**: 白飛びを抑えつつ明るく合成
    - **乗算 (Multiply)**: リムシェード（エッジの落ち込み・影表現）
- **パラメーター一覧**:
  - 有効化トグル (`_CustomRim2ndEnabled` / `_CustomRim2ndUIEnabled`)
  - リムカラー (`_CustomRim2ndColor`, HDR)
  - 強度 (`_CustomRim2ndStrength`)
  - 絞り・指数 (`_CustomRim2ndPower`, 0.1 〜 32.0)
  - 境界 (`_CustomRim2ndBorder`, 既定 0.5) / ぼかし (`_CustomRim2ndBlur`)
  - ブレンドモード (`_CustomRim2ndBlendMode`: 0=Replace, 1=Add, 2=Screen, 3=Multiply)
  - 法線影響度 (`_CustomRim2ndNormalStrength`)
  - 影減衰度 (`_CustomRim2ndShadowAttenuation`)
  - メインカラー乗算度 (`_CustomRim2ndMainColorStrength`)
  - ライティング反映 (`_CustomRim2ndEnableLighting`, 既定 1): リム色にライト色を乗算 (乗算モードは対象外)。暗所で発光して浮かないようにする
  - リムライトの方向 (`_CustomRim2ndVerticalBias`, -1〜1): ワールド上方向を基準に、+ で上向きの面、- で下向きの面だけにリムを出す
  - 逆光ブースト (`_CustomRim2ndBacklight`, 0〜4): 光源が視点の反対側にあるほどリムを強める (`saturate(-fd.vl)^2`)
  - 適用マスク (`_CustomRim2ndMaskTex`): ※エディタで自動パック（Pack 1 Gチャンネル）

---

### 2.6 追加リムライト (Rim Light 3rd)
Rim Light 2nd と同等の完全な第3のリムライト層です。独立したカラー、強度、指数、境界・ぼかし、ブレンドモード、方向バイアス、逆光ブースト、マスクを持ちます。

- **パラメーター一覧**:
  - 有効化トグル (`_CustomRim3rdEnabled` / `_CustomRim3rdUIEnabled`)
  - リムカラー (`_CustomRim3rdColor`, HDR)
  - 強度 (`_CustomRim3rdStrength`)
  - 絞り・指数 (`_CustomRim3rdPower`, 0.1 〜 32.0)
  - 境界 (`_CustomRim3rdBorder`, 既定 0.5) / ぼかし (`_CustomRim3rdBlur`)
  - ブレンドモード (`_CustomRim3rdBlendMode`: 0=Replace, 1=Add, 2=Screen, 3=Multiply)
  - 法線影響度 (`_CustomRim3rdNormalStrength`)
  - 影減衰度 (`_CustomRim3rdShadowAttenuation`)
  - メインカラー乗算度 (`_CustomRim3rdMainColorStrength`)
  - ライティング反映 (`_CustomRim3rdEnableLighting`, 既定 1)
  - リムライトの方向 (`_CustomRim3rdVerticalBias`, -1〜1)
  - 逆光ブースト (`_CustomRim3rdBacklight`, 0〜4)
  - 適用マスク (`_CustomRim3rdMaskTex`): ※エディタで自動パック（Pack 2 Gチャンネル）

---

## 3. テクスチャパラメータ64上限対策アーキテクチャ

### 3.1 課題と対策方針
- **ps_4_0 サンプラー制限 (16)**:
  - lilToon のグローバル共有サンプラー `sampler_linear_repeat`（および `lil_sampler_linear_clamp`）を全追加テクスチャで共有。シェーダー内の新規 SamplerState 宣言を 0 に抑える。
- **テクスチャパラメータ数制限 (64)**:
  - HLSL 内で `TEXTURE2D(...)` を個別に宣言すると、プロパティ数ではなく宣言テクスチャ数としてハードウェア上限を消費する。
  - 単一チャンネル（主に白黒マスク）として参照する4つのテクスチャを **1枚の RGBA テクスチャ（`_CustomMaskPacked`）** に統合する。
  - 各マスクは個別の Tiling/Offset（`_ST`）を持ち、シェーダー内では `_CustomMaskPacked` をそれぞれの UV でチャンネルサンプリングするため、機能制限（解像度やタイリングの自由度）は一切発生しない。
  - MatCap は前後半球とも1枚のテクスチャで賄う（後半球は鏡像）。
  - 結果として、シェーダーが宣言するテクスチャは **4枚**: `_CustomMaskPacked`, `_CustomMaskPacked2`, `_CustomNormal3rdTex`, `_CustomMatcapFrontTex`（旧構成は `_CustomMatcapBackTex` を含む5枚）。

### 3.2 パックドマスクのチャンネル割り当て
**Pack 1 (`_CustomMaskPacked`)**
| チャンネル | 割り当て元プロパティ | 機能名 | デフォルト値（未設定時） |
| :---: | :--- | :--- | :---: |
| **R** | `_CustomRefl2ndMaskTex` | スペキュラー 2nd マスク | `1.0` (White) |
| **G** | `_CustomRim2ndMaskTex` | リムライト 2nd マスク | `1.0` (White) |
| **B** | `_CustomNormal3rdMaskTex` | ノーマルマップ 3rd マスク | `1.0` (White) |
| **A** | `_CustomMatcapMaskTex` | MatCap マスク | `1.0` (White) |

**Pack 2 (`_CustomMaskPacked2`)**
| チャンネル | 割り当て元プロパティ | 機能名 | デフォルト値（未設定時） |
| :---: | :--- | :--- | :---: |
| **R** | `_CustomRefl3rdMaskTex` | スペキュラー 3rd マスク | `1.0` (White) |
| **G** | `_CustomRim3rdMaskTex` | リムライト 3rd マスク | `1.0` (White) |
| **B** | 未使用 | 予備 | `1.0` (White) |
| **A** | 未使用 | 予備 | `1.0` (White) |

### 3.3 エディタ側ライフサイクル（DennokoEx 方式準拠）
`Assets/dennokoworks/DennokoEx` の実装設計（永続 PNG アセット＋ビルドフック方式）を採用します。

1. **オーサリング UX の完全維持**:
   - マテリアルインスペクター上には、ユーザー向けに `_CustomRefl2ndMaskTex` 等の個別スロットをそのまま表示。
2. **バックグラウンド自動ベイク**:
   - `SpecularExMaskPacker`: GPU Blit（`MaskPacker.shader`）を用いて各テクスチャの R チャンネルを RGBA に結合し、PNG バイト列を生成。
   - `SpecularExPackedMaskStore`: 入力テクスチャの GUID・更新日時・バージョンからフィンガープリントハッシュを計算し、`Assets/dennokoworks/SpecularExV2_Generated/PackedMasks/<hash>.png` として永続アセットを保存。
   - 自動インポート設定（Linear, sRGB=false, 圧縮・Mipmap 設定）を適用し、マテリアルの `_CustomMaskPacked` に自動代入。
3. **アバタービルド時フック (`VRCSDK / BuildHook`)**:
   - VRChat の Build & Publish 前処理（`IVRCSDKPreprocessAvatarCallback`, `callbackOrder = 0`）にて、アバター内の全マテリアルとアニメーションクリップを検査し、最新のパックドテクスチャが確実に割り当てられていることを保証。

---

## 4. シェーダー構造とフック設計

### 4.1 lilToon テンプレート構成
lilToon カスタムシェーダーのコンテナ・ブロック方式を使用します。

```
Shaders/
├── custom.hlsl                     # 全機能のフラグメント処理ロジック
├── custom_insert.hlsl              # パス依存ヘルパー関数（ForwardAdd等）
├── lilCustomShaderDatas.lilblock   # シェーダー名・エディタクラス定義
├── lilCustomShaderProperties.lilblock # マテリアルプロパティ定義
├── lilCustomShaderInsert.lilblock  # custom_insert.hlsl のインクルード
├── SpecularEx_MaskPacker.shader    # マスクパッキング用 Blit シェーダー
└── lts*.lilcontainer               # 各描画モード（Opaque, Cutout, Trans 等）
```

### 4.2 フックポイントの配置と順序
lilToon のフラグメントシェーダーパイプラインに対して、以下の順序でフックを挿入します。

```
[lilToon Normal 1st / 2nd 計算]
      ↓
[BEFORE_AUDIOLINK] ───────→ ① 追加ノーマル (Normal Map 3rd) 合成
                             ・lilBlendNormal によるタンジェント空間ブレンド
                             ・DNKW_REFRESH_NORMAL_DERIVED で派生ベクトルを更新
      ↓
[lilToon Main Color & 影計算]
      ↓
[BEFORE_REFLECTION] ──────→ ② 追加スペキュラー (Specular 2nd)
                             ・ForwardBase / ForwardAdd での GGX/Blinn-Phong 計算
                             ・fd.col へのスペキュラー加算
      ↓
[BEFORE_RIMLIGHT] ────────→ ③ 追加 MatCap (World-Oriented Matcap)
                             ・ビュー法線とワールド反射ベクトルの参照方向ブレンドで1回サンプリング
                             ・各種ブレンドモードで fd.col に合成
      ↓
[lilToon Rim Light 計算]
      ↓
[BEFORE_EMISSION_1ST] ────→ ④ 追加リムライト (Rim Light 2nd)
                             ・視線フレネルによるリム項計算
                             ・4種ブレンドモードで fd.col に合成
      ↓
[lilToon Emission / 最終出力]
```

### 4.3 パス対応
- **ForwardBase**: 全機能がフル動作（ディレクショナルライト・環境光・GI を反映）。
- **ForwardAdd**:
  - 追加スペキュラー（Specular 2nd）が追加光源の方向・光色・減衰を反映して動作。
  - Normal Map 3rd が追加ライトの陰影計算に寄与。
  - MatCap / リムライトは設定に応じて加算または無効化。
- **ShadowCaster / Meta**: 不要な計算を自動バイパスし、描画パフォーマンスを維持。

---

## 5. UI / Inspector 設計方針

### 5.1 Dual-Property パターン
テクスチャ未設定時や機能 OFF 時に GPU 分岐や不要なサンプリングを発生させないため、UI 表示用プロパティと実動作プロパティを分離します。

- `_CustomXxxUIEnabled`: インスペクター上のチェックボックス
- `_CustomXxxEnabled`: シェーダーに渡される実効フラグ（UI が ON かつテクスチャ等の必要条件が揃っている場合のみ 1 になる）
- `SyncEffectiveEnabled()` により、インスペクター描画時や値変更時に自動同期。

### 5.2 インスペクターセクション構成
`SpecularExV2Inspector`（`lilToonInspector` 派生）を実装し、以下の構成で lilToon の UI に自然に統合します。

1. **追加スペキュラー (Specular 2nd / 3rd)**: 有効化、色、強度 ／ タイプ、スムースネス、クリアコート (ON 時はメタリック・反射率を隠す)、フレネル ／ ライティング反映、光源方向の補正 ／ 法線強度、影減衰、メインカラー反映、ForwardAdd適用 ／ マスク
2. **追加 MatCap**: 有効化、テクスチャ、ワールド固定 ／ 色、強度、ブレンドモード、メインカラー、HSVG ／ ぼかし、回転 (ワールド固定 > 0 のとき)、法線強度 ／ ライティング/影反映、裏面 ／ マスク
3. **追加ノーマル (Normal Map 3rd)**: 有効化、ノーマルマップ、スケール、UV選択、スクロール/角度/回転速度 ／ 距離フェード ／ マスク
4. **追加リムライト (Rim Light 2nd)**: 有効化、色、強度、ブレンドモード、ライティング反映 ／ Power、境界、ぼかし ／ 上下制限、逆光ブースト ／ 法線強度、影減衰、メインカラー反映 ／ マスク
5. **マスクパッキング状態 (Mask Packing Status)**: 自動パックの稼働状態、現在のフィンガープリント、手動強制再ベイクボタン

---

## 6. 実装フェーズとロードマップ

- [ ] **Phase 1: 仕様確定とプロジェクト基盤整理**
  - 本要件仕様書（`overview.md`）のレビューと確定
  - ディレクトリ構成および ASMDEF（Editor / Runtime）の設計
- [x] **Phase 2: エディタマスクパッカー機構の構築**
  - `MaskPacker.shader` の作成
  - `SpecularExMaskPacker.cs` & `SpecularExPackedMaskStore.cs` の実装
  - `SpecularExPackedMaskWatcher.cs` による変更検知と自動ベイク
- [x] **Phase 3: シェーダープロパティとフック（HLSL）の実装**
  - `lilCustomShaderProperties.lilblock` のプロパティ定義
  - `custom.hlsl` への各機能ロジック実装（Normal 3rd, Specular 2nd, World Matcap, Rim 2nd）
  - ForwardAdd パス対応コード（`custom_insert.hlsl`）の実装
- [x] **Phase 4: マテリアルインスペクター UI 実装**
  - `SpecularExV2Inspector.cs` の実装とローカライズ（日本語/英語）
  - Dual-Property 同期と設定コピー/ペースト機能の実装
- [ ] **Phase 5: 動作検証と負荷テスト**
  - 不透明 (Opaque)、カットアウト (Cutout)、半透明 (Transparent) での描画検証
  - VRChat Build & Publish 時のテクスチャパラメータ数 64 上限検証
  - 各種ライティング環境（リアルタイムライト、ライトプローブ、VRCLV 等）での挙動検証



