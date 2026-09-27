# SpecularExV2 シェーダービルド時間削減計画 (A & B)

作成日: 2026-09-27  
対象ファイル: [custom_insert.hlsl](../../Shaders/custom_insert.hlsl), [custom.hlsl](../../Shaders/custom.hlsl)

---

## 1. 背景と目的

VRChat アバターのアップロード時や Unity の AssetBundle ビルド時において、「Building Asset Bundle」フェーズの所要時間が増加する課題がある。

### 根本原因の要約
1. **lilToon のビルド時機能ストリッピングが効かない**:
   通常の lilToon（`.shader`）はビルド直前（`SetShaderSettingBeforeBuild`）にアバターで未使用の機能を `#define` レベルで削除・インライン化するが、`.lilcontainer` は [lilToonSetting.cs:531](file:///c:/Users/dennoko/AppData/Local/VRChatCreatorCompanion/VRChatProjects/lilToon_Extension_v2/Packages/jp.lilxyzw.liltoon/Editor/lilToonSetting.cs#L531) で除外されているため、**エディタ設定上の全機能が有効化された最大規模のシェーダー**としてコンパイルされる。
2. **実行時一様分岐（`if`）によるコード肥大化**:
   バリアント爆発を避けるために 7 機能をマテリアルプロパティ（`_CustomRefl2ndEnabled > 0.5` 等）による実行時分岐で記述しているため、マテリアル上で機能が OFF であっても、コンパイラは全パス・全バリアントでコード全体の構文解析・最適化・レジスタ割り当てを行う必要がある。
3. **不要パスでの不要な展開**:
   本来拡張機能が不要な `SHADOW_CASTER` や `META` パス、およびバリアント数が膨大な `FORWARD_ADD`（追加ライト）パスにまで重厚な処理が及んでいる。

本計画では、C# 側の複雑なビルドパイプライン改修を行わず、**拡張シェーダー（HLSL）側のプリプロセッサガード（`#if` / `#define`）によるシンプルかつ低リスクな実装**でコンパイル負荷を削減する計画 (A) および (B) を定義する。

---

## 2. 計画 (A): ShadowCaster および Meta パスからの完全除外

### 2.1 現状の課題
- lilToon のコンテナ展開仕様（`Default.lilblock`）により、`*LIL_SUBSHADER_INSERT*` は `FORWARD` だけでなく `SHADOW_CASTER`, `SHADOW_CASTER_OUTLINE`, `META` パスにも挿入される。
- [custom_insert.hlsl](../../Shaders/custom_insert.hlsl) の冒頭で以下が無条件に定義されている：
  ```hlsl
  #define LIL_V2F_FORCE_TANGENT
  #define LIL_V2F_FORCE_BITANGENT
  #define LIL_REQUIRE_APP_TANGENT
  ```
  - これにより、影パスや Meta パスでも頂点シェーダーでの接線計算や補間器（TEXCOORD）が強制される。
  - さらに、ピクセルシェーダーで視差計算（`LIL_GET_PARALLAX_DATA`）のコードまで混入する。
- `custom_insert.hlsl` に定義された大量の関数群（GGX、Blinn-Phong、MatCap UV 計算、ToneCorrection 等）が、影や Meta パスのコンパイル単位でも無駄に構文解析される。
- なお、`LIL_V2F_FORCE_BITANGENT` は lilToon 2.3.4 では参照先がなく、完全に不要な定義として残っている。

### 2.2 改修内容
1. **`custom_insert.hlsl` のパス別ガード**:
   `LIL_PASS_SHADOWCASTER` または `LIL_PASS_META` が定義されている場合、拡張シェーダーの接線定義および全関数定義をスキップする。
2. **未使用マクロの削除**:
   `#define LIL_V2F_FORCE_BITANGENT` を削除する。
3. **`custom.hlsl` のフック安全化**:
   `BEFORE_AUDIOLINK` 等のフックマクロが影パス等で参照された場合でも、何も処理しないようにガードする。

### 2.3 コード変更イメージ

```hlsl
// custom_insert.hlsl の冒頭

#ifndef DNKW_SPEX_CUSTOM_INSERT_INCLUDED
#define DNKW_SPEX_CUSTOM_INSERT_INCLUDED

#if defined(LIL_PASS_SHADOWCASTER) || defined(LIL_PASS_META)
    // 影落とし・ライトマップベイクパスでは、接線入力も拡張機能の全ロジックも不要
    #define DNKW_PASS_FORWARDADD 0
    #define DNKW_PASS_META       1
#else

    // Normal Map 3rd/4th のため接線を要求（Forward パス系のみ）
    #define LIL_V2F_FORCE_TANGENT
    #define LIL_REQUIRE_APP_TANGENT

    #if defined(LIL_PASS_FORWARDADD)
        #define DNKW_PASS_FORWARDADD 1
    #else
        #define DNKW_PASS_FORWARDADD 0
    #endif
    #define DNKW_PASS_META 0

    // ... (既存の共通関数群・Specular/MatCap のロジック) ...

#endif // !defined(LIL_PASS_SHADOWCASTER) && !defined(LIL_PASS_META)
#endif // DNKW_SPEX_CUSTOM_INSERT_INCLUDED
```

### 2.4 効果とリスク
- **削減効果**:
  ShadowCaster（基本＋アウトライン）および Meta パスにおいて、頂点接線変換・補間器消費・不要関数のコンパイルが完全に排除される。
- **リスク**: **ゼロ**。
  影パスは深度・シャドウマップ書き込みのみであり、Meta パスもライトマップ焼き込み用（DNKW_PASS_META で元々リムライト等を除外済み）であるため、アバターの見た目への影響は一切ない。

---

## 3. 計画 (B): ForwardAdd（追加ライト）パスからの World MatCap 除外

### 3.1 現状の課題
- ForwardAdd パスは、追加ライトの種別や環境により膨大なバリアントを持つ：
  - 光源種別（`POINT`, `DIRECTIONAL`, `SPOT`, `POINT_COOKIE`, `DIRECTIONAL_COOKIE` の 5 種）
  - フォグ（4 種）
  - インスタンシング（2 種）
  $\rightarrow$ **1 パスあたり 40 バリアント**（アウトライン版 `FORWARD_ADD_OUTLINE` もある場合は **計 80 バリアント**）。
- 現在、MatCap は `_CustomMatcapApplyFA` というマテリアルプロパティ（一様変数）で判定しているため、**ForwardAdd の 40〜80 バリアントすべてに MatCap の重厚なコードがコンパイルされている**：
  - ワールド反射ベクトル計算
  - `sincos` による Yaw 回転
  - `SafeNormalize` による方向合成
  - 明示 LOD サンプリング（`LIL_SAMPLE_2D_LOD`）
  - HSVG トーン補正、ライティング乗算、ブレンド計算
- World MatCap は「ワールド空間に固定された周囲環境（擬似 Cubemap）」であり、追加のスポットライトやポイントライトごとに重ねて加算合成する表現上の必要性が極めて低い。

### 3.2 改修内容
1. **プリプロセッサによる静的除外**:
   ForwardAdd パス（`#if defined(LIL_PASS_FORWARDADD)`）では、`BEFORE_RIMLIGHT` 内の MatCap 処理をまるごと展開しない（または空にする）。
2. **コンパイラからの完全消去**:
   一様変数による実行時 `if` ではなくプリプロセッサマクロで括ることで、ForwardAdd の全 40〜80 バリアントから MatCap のサンプリング命令および計算コードを完全に除去する。

### 3.3 コード変更イメージ

```hlsl
// custom.hlsl: BEFORE_RIMLIGHT 付近

#if defined(LIL_PASS_FORWARDADD)
    // ForwardAdd（追加ライトパス）では環境反射（World MatCap）を展開しない
    #define BEFORE_RIMLIGHT
#else
    #define BEFORE_RIMLIGHT \
        if (_CustomMatcapEnabled > 0.5 && _CustomMatcapAlpha != 0.0 && _CustomMatcapColor.a != 0.0) { \
            float3 _wmN   = normalize(lerp(fd.origN, fd.matcapN, _CustomMatcapNormalStrength)); \
            float  _wmT   = saturate(_CustomMatcapWorldFixed); \
            float3 _wmDV  = 0.0; \
            float3 _wmDW  = 0.0; \
            if (_wmT < 1.0) _wmDV = mul(fd.cameraMatrix, _wmN); \
            if (_wmT > 0.0) _wmDW = DNKW_RotateYaw(reflect(-fd.V, _wmN), _CustomMatcapWorldRotation); \
            float2 _wmUV  = DNKW_MatcapUV(_wmDV, _wmDW, _wmT); \
            float4 _wmTex = LIL_SAMPLE_2D_LOD(_CustomMatcapFrontTex, lil_sampler_linear_clamp, _wmUV, _CustomMatcapBlur); \
            float3 _wmRGB = DNKW_ToneCorrection(_wmTex.rgb, _CustomMatcapHSVG); \
            _wmRGB *= _CustomMatcapColor.rgb * lerp(float3(1.0, 1.0, 1.0), fd.albedo, _CustomMatcapMainColorStrength); \
            float3 _wmCol = DNKW_MatcapLighting(_wmRGB, fd.lightColor, _CustomMatcapEnableLighting, _CustomMatcapBlendMode); \
            float  _wmA   = _wmTex.a * _CustomMatcapColor.a * _CustomMatcapAlpha * DNKW_SAMPLE_MASK(_CustomMatcapMaskTex_ST).a; \
            DNKW_APPLY_NOISE(_wmA, _CustomMatcapNoiseST, _CustomMatcapNoiseStrength) \
            _wmA *= lerp(1.0, fd.shadowmix, _CustomMatcapShadowStrength); \
            _wmA  = (_CustomMatcapDisableBackface > 0.5 && fd.facing < 0.0) ? 0.0 : _wmA; \
            fd.col.rgb = lilBlendColor(fd.col.rgb, _wmCol, _wmA, _CustomMatcapBlendMode); \
        }
#endif
```

### 3.4 効果とリスク
- **削減効果**:
  コンパイル負荷の最も重い ForwardAdd パス（40〜80 バリアント）から MatCap のサンプリング、Yaw 回転、ベクトル演算が 100% 削除され、命令数およびレジスタ消費が大幅に縮小する。
- **表現上の影響**:
  - MatCap はディレクショナルライト等のメイン光（ForwardBase）で通常どおり描画される。
  - 追加ライト（ポイントライトなど）に照らされた際、MatCap が二重・三重に加算される挙動がなくなるが、環境反射としての自然さはむしろ向上する。
  - `_CustomMatcapApplyFA` プロパティは互換性維持のためマテリアル上残しても問題ないが、実質的に常に 0（無効）として扱われる。

---

## 4. 実施手順と検証項目

### 4.1 実装手順
1. `Shaders/custom_insert.hlsl` に計画 (A) のガードを追加し、`LIL_V2F_FORCE_BITANGENT` を削除する。
2. `Shaders/custom.hlsl` に計画 (B) の `#if defined(LIL_PASS_FORWARDADD)` ガードを適用する。
3. `custom_insert.hlsl` 側の不要になった `DNKW_Refl2ndPassEnabled` 等の参照を整理する。

### 4.2 検証項目
1. **コンパイル検証**:
   - `fxc`（または Unity エディタ上）で ForwardBase, ForwardAdd, Outline, ShadowCaster, Meta の各パスが正常にエラーなくコンパイルできること。
2. **描画・機能検証**:
   - 通常シーンでアバターのメイン描画（Specular, MatCap, Normal, Rim）が以前と完全に同値であること。
   - 影の描画（ShadowCaster）が正常に落ちること。
   - ポイントライト・スポットライトを配置した状態で、追加ライトのライティング（Specular 2nd 等）が正常に加算され、エラーや破綻がないこと。
3. **ビルド時間測定**:
   - VRChat アバタービルド（または AssetBundle ビルド）を実行し、「Building Asset Bundle」フェーズの所要時間が短縮されていることを確認する。

---

## 5. 実装状況（2026-09-27）と計画からの修正点

(A)(B) とも実装済み。上記のコード変更イメージには次の誤りがあったため、以下のとおり修正して実装した。

1. **(B) の `#if` は custom.hlsl では効かない**:
   custom.hlsl はシェーダーレベルの `HLSLINCLUDE` から読み込まれ、各パスが `LIL_PASS_FORWARDADD` を定義するより前に展開されるため、そこでの `#if defined(LIL_PASS_FORWARDADD)` は常に偽になる。
   → custom_insert.hlsl（パス定義の後、`lil_pass_*.hlsl` の前に挿入される）で `#undef BEFORE_RIMLIGHT` / `#define BEFORE_RIMLIGHT` として空に再定義した。フックマクロは `lil_pass_*.hlsl` で展開されるため、ここでの再定義が有効になる。
2. **(A) のままでは Meta パスがコンパイルエラーになる**:
   `lil_pass_meta.hlsl` は `BEFORE_EMISSION_1ST` を 2 回展開し、そのマクロは custom_insert.hlsl の `DNKW_MaskCache` / `DNKW_SampleMask2Cached` を参照する。関数定義だけをスキップすると未定義エラーになる。
   → Meta / ShadowCaster 分岐で `BEFORE_EMISSION_1ST` を空に再定義した。これでリムのライトマップ焼き込み除外も兼ねるため、`DNKW_PASS_META` / `DNKW_PASS_FORWARDADD` は不要となり削除した。
3. **(B) は既定の見た目を変える**:
   `_CustomMatcapApplyFA` の既定値は 1 のため、既存マテリアルでも追加ライト下の MatCap 加算がなくなる。インスペクターのトグルを削除し、プロパティは互換のため残した。USAGE / リリースノート草案も更新済み。
4. **効果の見込み**:
   BRP の ShadowCaster / Meta はもともと接線を v2f に含めず（`LIL_V2F_FORCE_TANGENT` は forward 系のみ参照）、バリアント数も少ないため、(A) の短縮効果は小さい。主な短縮は (B) による ForwardAdd（光源 5 種 × フォグ × インスタンシング、アウトライン版含む）の命令削減。

未実施の検証: Unity 上での全パスのコンパイル確認、追加ライト下の描画確認、ビルド時間の実測（4.2 参照）。
