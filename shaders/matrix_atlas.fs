// matrix_atlas.fs - Matrix-style glyph rain drawn from a font atlas.
// Texture convention: the sampler NAME is the FILE NAME - the saver looks for
// font_atlas_512.png next to this shader (then in the shader folder, then in
// the app data dir). An 8-bit grayscale PNG is fine: it decodes to equal
// RGB channels and the glyph is read from .r below.
//
// Atlas layout (font_atlas_512.png): 512x512, 16x16 grid of 32px cells,
// white glyphs on black, glyphs in the TOP 6 rows of the atlas.
//
// Texture reference cheat sheet:
//   Shadertoy-style pasted code works unchanged: an undeclared iChannelN
//   reference is auto-declared and loads iChannelN.png. This shader declares
//   its sampler explicitly instead (font_atlas_512 -> font_atlas_512.png).
//   vec4 tex = texture(iChannelN, uv);    // uv in 0..1 - same as Shadertoy

uniform sampler2D font_atlas_512;

const float COLS = 16.0;       // atlas columns
const float ATLAS = 16.0;      // atlas rows (full grid)
const float GLYPH_ROWS = 6.0;  // glyphs live in the top 6 atlas rows

// cheap per-index random in 0..1
float hash(float n) { return fract(sin(n * 127.1) * 43758.5453); }

void main()
{
    // pixel coords (y up) and a cell grid sized to the screen height
    vec2 pc = fragTexCoord.xy * iResolution.xy;
    float S = floor(iResolution.y / 28.0) + 1.0;   // glyph cell size in px
    vec2 cell = floor(pc / S);
    vec2 local = fract(pc / S);                    // 0..1 inside the cell
    float rows = iResolution.y / S;

    // per-column behaviour
    float seed = cell.x * 0.618 + 3.7;
    float speed = mix(3.0, 9.0, hash(seed));
    float phase = hash(seed * 1.7) * 12.0;
    float tail = rows + 8.0;

    // rain head moves DOWN: measured in rows from the top edge
    float head = mod(iTime * speed + phase, tail);
    float d = head - (rows - cell.y);              // rows below the head

    // outside the trail: faint static glimmer so columns never go fully dark
    float glow = exp(-max(d, 0.0) * 0.55) * step(0.0, d);
    float flicker = step(hash(cell.x + cell.y * 7.3 + floor(iTime * 11.0)), 0.93);

    // pick a glyph cell from the atlas; the glyph changes over time
    float glyphT = floor(iTime * (1.0 + 3.0 * hash(seed + cell.y)) + cell.y * 3.1);
    float gcol = floor(hash(glyphT * 1.23 + seed) * COLS);
    float grow = floor(hash(glyphT * 2.71 + seed) * GLYPH_ROWS);

    // atlas cell -> uv. The loader flips rows (uv.y up), so PNG row 0 (top)
    // sits at v = 15/16..1. local.y=1 (screen top of the cell) must sample
    // the top of the glyph, hence + local.y in v.
    vec2 auv = vec2((gcol + local.x) / COLS,
                    (15.0 - grow + local.y) / ATLAS);
    float glyph = texture(font_atlas_512, auv).r;  // white-on-black -> red ch.

    // head glyph is near-white, trail fades to deep green
    vec3 tint = mix(vec3(0.10, 0.85, 0.30), vec3(0.85, 1.0, 0.85), step(d, 1.0));
    vec3 col = tint * glyph * glow * flicker;
    col += vec3(0.02, 0.14, 0.05) * flicker;       // dim idle phosphor
    finalColor = vec4(col, 1.0);
}
