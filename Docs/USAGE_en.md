# SpecularExV2 Feature & Parameter Reference Guide

This document describes the custom features and extended parameters introduced in **SpecularExV2**.  
Standard features shared with lilToon (Main Color, basic shadows, standard Reflection 1st, etc.) are omitted. **Only the custom features and unique parameters specific to this shader** are covered.

This guide provides concise, practical explanations of the purpose of each parameter and how modifying its value affects material appearance.

---

## Table of Contents

1. [Specular 2nd / 3rd (Additional Highlights)](#1-specular-2nd--3rd-additional-highlights)
2. [World-Oriented MatCap (Environmental Reflection)](#2-world-oriented-matcap-environmental-reflection)
3. [Normal Map 3rd / 4th (Additional Bumps & Animation)](#3-normal-map-3rd--4th-additional-bumps--animation)
4. [Rim Light 2nd / 3rd (Additional Edge Lights & Rim Shadows)](#4-rim-light-2nd--3rd-additional-edge-lights--rim-shadows)
5. [Noise Mask (Shared) (Roughness & Sparkle)](#5-noise-mask-shared-roughness--sparkle)
6. [Utility Features (Automatic Mask Packing & Section Copy/Paste)](#6-utility-features)

---

## 1. Specular 2nd / 3rd (Additional Highlights)

Provides **two additional independent highlight layers (2nd / 3rd)** on top of lilToon's primary "Reflection" layer.

> **Primary Use Cases:**
> - Layering a broad, soft sheen with a tight, sharp pinpoint highlight
> - Simulating a clear resin or glossy top-coat layer (Clear Coat) over base materials
> - Preserving consistent hair or eye highlights independent of the world light direction

### Custom Parameters

#### Clear Coat
- **Purpose**: Simulates transparent protective coatings such as automotive top-coat, varnish, clear resin, or polished lacquer.
- **Effect**:
  - **Enabled (ON)**: Fixes reflectance to an optimal clear-coat value (0.04) and automatically dims the underlying base layer at glancing angles. Metallic and Reflectance controls are hidden.
  - **Disabled (OFF)**: Operates as a standard specular layer with configurable Metallic and Reflectance properties.

#### Fresnel Strength / Fresnel Power
- **Purpose**: Controls the intensity and falloff of reflections on surfaces angled away from the camera (glancing angles).
- **Effect**:
  - **Fresnel Strength**: Higher values increase reflection intensity at steep viewing angles (0 provides uniform reflection across all angles).
  - **Fresnel Power**: Higher values confine the reflection to the outer boundary contour. Lower values allow the reflection to spread across broader angles.

#### Enable Lighting
- **Purpose**: Determines the degree to which ambient scene lighting affects the highlight.
- **Effect**:
  - **1.0 (Default)**: Highlights dim in dark environments and inherit the color of scene lighting.
  - **0.0**: Highlights maintain constant brightness regardless of ambient light level (suitable for stylized anime visuals or self-illuminating materials).

#### Light Direction Override / Override Direction
- **Purpose**: Simulates specular highlights from a custom directional source regardless of world lighting orientation.
- **Effect**:
  - **Light Direction Override (0.0 to 1.0)**: Blends the effective light direction from the scene's primary directional light toward the direction specified by Override Direction (ambient brightness levels are preserved).
  - **Override Direction (Camera-relative: X=Right, Y=Up, Z=Forward)**: Specifies the reference pseudo-light direction. For example, `(0, 0.5, 1)` places the light slightly above and in front of the viewer, ensuring stable highlights across changing camera angles.

#### Noise Strength / Tiling / Offset
- **Purpose**: Imparts micro-roughness or granular sparkle across the highlight rather than a perfectly smooth sheen.
- **Effect**: Samples the texture specified in [Noise Mask (Shared)](#5-noise-mask-shared-roughness--sparkle).
  - **Noise Strength**: Higher values increase modulation by the noise pattern.
  - **Tiling / Offset**: Adjusts the scale and positioning of the noise pattern.

---

## 2. World-Oriented MatCap (Environmental Reflection)

Standard MatCaps adhere to camera view space, causing reflection patterns to remain static relative to the screen. This feature provides a **pseudo-environmental reflection anchored to world space** using a single texture.

> **Primary Use Cases:**
> - Imparting environmental reflections anchored to the surrounding scene onto metallic surfaces or accessories
> - Producing natural reflection motion responding to avatar movement and camera rotation
> - Adjusting texture coloration and contrast within Unity without re-exporting image assets

### Custom Parameters

#### World Fixing
- **Purpose**: Selects whether reflections track camera view space or remain anchored to world coordinates.
- **Effect**:
  - **0.0 (View Tracking)**: Operates as a conventional MatCap anchored to camera view space.
  - **1.0 (World Fixed)**: Anchors reflections to world space coordinates; reflections smoothly translate as the object rotates.
  - **0.1 to 0.9 (Blend)**: Interpolates between view-tracking and world-anchored directions, creating a smooth delayed tracking response.

#### World Rotation (Yaw)
- **Purpose**: Adjusts the horizontal orientation (azimuth) of the reflection when World Fixing is active (> 0).
- **Effect**: Rotates the reflection pattern around the vertical Y-axis to orient highlights or scenery reflections toward a specific direction.

#### Hue / Saturation / Value / Gamma (HSVG)
- **Purpose**: Performs color grading on the MatCap texture directly within the material inspector.
- **Effect**:
  - **Hue**: Shifts the overall color tone.
  - **Saturation**: Adjusts color vibrancy (0 produces grayscale).
  - **Value**: Adjusts brightness.
  - **Gamma**: Adjusts mid-tone contrast curves.

#### Main Color Power
- **Purpose**: Controls the multiplication ratio of the base texture color into the MatCap reflection.
- **Effect**: Higher values blend the underlying surface texture color more strongly, improving integration with colored surfaces.

#### Additional Lights
- The MatCap is an environment reflection and is drawn only in the main light pass (ForwardBase). Additional point or spot lights (ForwardAdd pass) do not draw it, so it never stacks up per light.

---

## 3. Normal Map 3rd / 4th (Additional Bumps & Animation)

Allows compositing a **3rd and 4th normal layer** on top of lilToon's standard slots (1st / 2nd) without overwriting existing textures.

> **Primary Use Cases:**
> - Overlaying detail normal maps (water streaks, embroidery, surface wear) while preserving primary skin and cloth normals
> - Adding animated UV scrolling or rotational effects to normal patterns
> - Reducing distance-based aliasing and moiré shimmering in VR environments

### Custom Parameters

#### UV Mode (`UV0` / `UV1` / `UV2` / `UV3`)
- **Purpose**: Selects the target UV coordinate channel.

#### UV Scroll / Angle / Rotation Speed
- **Purpose**: Configures rotation angle and motion animations for the normal pattern.
- **Effect**:
  - **Scroll (X / Y)**: Translates the texture continuously across the surface (rain drops, fluid flow, scan lines).
  - **Angle**: Sets a static rotation offset.
  - **Rotation Speed**: Applies continuous rotational animation.

#### Distance Fade Start / End / Strength
- **Purpose**: Attenuates normal intensity based on camera distance to reduce distant pixel shimmering and moiré artifacts.
- **Effect**:
  - **Distance Fade Start (m)**: Camera distance where normal attenuation begins.
  - **Distance Fade End (m)**: Camera distance where normal intensity reaches its minimum level.
  - **Distance Fade Strength**: The attenuation ratio at or beyond the end distance (1.0 produces a completely flattened surface).
  > **Example Setup:** For fine fabric or micro-weave patterns, setting Start to `2.0m`, End to `6.0m`, and Strength to `1.0` preserves close-up detail while suppressing distant shimmering.

---

## 4. Rim Light 2nd / 3rd (Additional Edge Lights & Rim Shadows)

Adds **two additional independent rim light layers (2nd / 3rd)** to the material.

> **Primary Use Cases:**
> - Layering contrasting rim lights (e.g. overhead daylight combined with ground reflection)
> - Applying contour shadowing (rim shade) via multiply blending
> - Automatically amplifying rim intensity under backlit conditions

### Custom Parameters

#### Blend Mode
- **Purpose**: Specifies the blending mode for the rim light layer.
- **Effect**:
  - **Normal**: Linearly interpolates the base color with the rim color.
  - **Add**: Adds rim light brightness to the surface (strong highlights/illumination).
  - **Screen**: Softly blends luminance while preventing harsh clipping.
  - **Multiply**: Darkens the contour boundary. Useful for stylized cel-shadow outlines or edge-darkening on textured fabrics such as velvet.

#### Rim Light Direction (+up / -down) (Vertical Bias)
- **Purpose**: Restricts rim light distribution based on the vertical world axis.
- **Effect**:
  - **Positive (+) values**: Restricts rim light to upward-facing surfaces (overhead or sky lighting).
  - **Negative (-) values**: Restricts rim light to downward-facing surfaces (ground bounce light).
  - **0.0**: Distributes rim light uniformly around the entire silhouette.

#### Backlight Boost
- **Purpose**: Automatically increases rim light intensity when viewing against the primary light source (backlit conditions).
- **Effect**: Amplifies contour lighting under backlighting while maintaining standard intensity under direct front lighting.

#### Border / Blur
- **Purpose**: Adjusts the distribution width and edge hardness of the rim light.
- **Effect**:
  - **Border**: Adjusts the threshold boundary where rim light appears.
  - **Blur**: Adjusts the falloff smoothness. Higher values yield a soft gradient; lower values create a sharp, cel-like boundary.

#### Enable Lighting
- **Purpose**: Determines whether the rim light color is modulated by scene lighting.
- **Effect**:
  - **1.0 (Default)**: Rim lighting dims naturally in dark environments.
  - **0.0**: Maintains constant intensity regardless of darkness (inactive when Blend Mode is set to Multiply).

---

## 5. Noise Mask (Shared) (Roughness & Sparkle)

A single grayscale texture assigned to the **"Noise Mask (Shared)"** slot at the bottom of the Inspector can be shared across all custom layers.

- **Specular 2nd / 3rd**: Breaks up specular highlights to simulate glitter or rough surface sheen
- **MatCap**: Adds brushed, hairline, or frosted grain to reflections
- **Rim Light 2nd / 3rd**: Disperses contour light boundaries into particle-like noise

> **Note:**  
> While the texture slot is shared, Tiling and Offset are configured independently within each layer, allowing distinct scale and density per feature from a single texture.

---

## 6. Utility Features

### Automatic Mask Packing
- **Overview**:
  To prevent exceeding hardware/API texture parameter limits (64 textures) during avatar upload, single-channel masks assigned to individual features are automatically packed into RGBA textures in the background.
- **Operation**:
  No manual packing step is required. Textures assigned to individual mask slots are packed automatically.  
  If external texture edits are not reflected immediately, click the **"Rebake Packed Masks"** button in the "Mask Packing Status" section.

### Section Copy & Paste
Click the **"…" (menu icon)** at the right side of any section header to copy and transfer settings between layers.
- **Copy**: Copies all numerical and color parameters to the clipboard (excludes texture references).
- **Copy (with textures)**: Copies all parameters including texture references.
- **Paste**: Applies the copied settings to the target section.
