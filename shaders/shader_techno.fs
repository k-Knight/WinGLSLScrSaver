#version 330
// =====================================================================
//  RETRO TECHNO TUNNEL v3 — tear-free, camera-locked, fully tunable
//  poison green <-> electric cyan | beat-synced | world-locked camera
//  Ported for kerneltrick's Shadertoy screensaver (raylib-style harness)
// =====================================================================

// --------------------- HARNESS (matches appolian.fs) ------------------
in vec2 fragTexCoord;          // vertex shader input from the app
uniform vec3 iResolution;      // viewport resolution (set by the app)
uniform float iTime;           // seconds (set by the app)
out vec4 finalColor;           // fragment output

// ------------------------- ADVANCEMENT -------------------------------
#define SPEED          40.0     // camera speed through the tunnel (units/s)
#define SPACING        3.5    // world distance between rectangle frames
#define NUM_FRAMES     10.0    // frames rendered ahead of the camera
#define FOG_NEAR       3.0     // distance where frames start dissolving
#define FOG_FAR        25.0    // fully fogged; keep >= NUM_FRAMES*SPACING
#define PERSP          1.0    // perspective strength (higher = faster shrink)
#define FOCAL          0.9     // zoom: higher = narrower FOV

// ------------------------- FRAME SHAPE --------------------------------
#define FRAME_W        1.78    // half-width  of each rectangle
#define FRAME_H        1.00    // half-height of each rectangle
#define FRAME_THICK    0.028   // neon line thickness
#define FRAME_VAR      0.08    // per-frame size randomness (0 = identical)

// ------------------------- GLOW INTENSITIES --------------------------
#define I_CORE         1.9     // neon core brightness
#define I_HALO        1.35     // outer glow halo
#define I_CORNER       2.75    // corner bracket brightness
#define I_TICKS        0.75    // edge tick brightness
#define CHROMA         0.025   // chromatic aberration (0 = off)
#define PULSE_SPEED    10.0     // energy pulse travel rate
#define PULSE_AMP      3.0    // pulse brightness swing

// ------------------------- DUST STREAKS -------------------------------
#define DUST_LAYERS    13.0    // streak layer count (density)
#define DUST_SPACING   0.31    // smaller = denser, more parallax events
#define DUST_BRIGHT    0.30    // streak base brightness
#define DUST_TAIL      0.55    // motion-blur streak length

// ------------------------- CAMERA MOTION ------------------------------
#define PATH_AMP       1.0     // tunnel curve strength (0 = dead straight)
#define PATH_FREQ      1.0     // curve frequency (>1 = tighter turns)
#define DRIFT_X        0.15    // camera look-around, x (auto-clamped inside!)
#define DRIFT_Y        0.08    // camera look-around, y (auto-clamped inside!)
#define DRIFT_SPEED    0.6     // look-around speed
#define ROLL_SPEED     0.0    // constant camera roll (rad/s)
#define ROLL_WOBBLE    0.35    // oscillating roll amount (0 = steady spin)
#define ROLL_W_FREQ    0.51    // roll oscillation speed

// ------------------------- BEAT / CRT / POST ---------------------------
#define BPM            190.0   // beat sync (affects pump + dust flare)
#define BEAT_PUMP      0.025   // FOV pump per beat (0 = off)
#define SCAN_AMP       0.10    // scanline depth
#define RAYS_AMP       0.20    // vanishing-point ray brightness
#define VIGNETTE       0.45    // edge darkening
#define GAMMA          1.18    // contrast curve
#define EXPOSURE       1.55    // HDR tone-map exposure
#define POSTERIZE      24.0    // color steps (higher = smoother, 0 = off)

// ------------------------- PALETTE -------------------------------------
#define GREEN  vec3(0.22, 1.00, 0.12)
#define CYAN   vec3(0.00, 0.90, 1.00)
#define PALETTE_SPEED 0.45     // green <-> cyan cycle rate

// ======================================================================

mat2 rot(float a) { float s = sin(a), c = cos(a); return mat2(c, -s, s, c); }

float sdFrame(vec2 p, vec2 b, float t) {
    vec2 d = abs(p) - b;
    return abs(length(max(d, 0.0)) + min(max(d.x, d.y), 0.0)) - t;
}

float hash(float n) { return fract(sin(n) * 43758.5453); }
float ign(vec2 p)   { return fract(52.6529689 * fract(p.x * 0.06711056 + p.y * 0.00583715)); }

// Stationary world-space tunnel path — the camera flies THROUGH it
vec2 pathAt(float x) {
    return vec2(sin(x * 0.163 * PATH_FREQ) * 0.52 + sin(x * 0.041 * PATH_FREQ) * 0.65,
                cos(x * 0.117 * PATH_FREQ) * 0.40 + sin(x * 0.071 * PATH_FREQ) * 0.25)
           * PATH_AMP;
}

void mainImage(out vec4 fragColor, in vec2 fragCoord) {
    vec2 uv = (fragCoord - 0.5 * iResolution.xy) / iResolution.y;
    uv *= 1.0 + 0.05 * dot(uv, uv);          // CRT curvature

    // --- Camera: ONE grid for positions AND ids => no tearing, no escape --
    float cam  = iTime * SPEED;              // continuous, never wrapped
    float grid = cam / SPACING;              // frame-grid coordinate (the fix)
    float fr   = fract(grid);
    float gb   = floor(grid);

    float kick = pow(1.0 - fract(iTime * BPM / 60.0), 4.0);

    // ONE global roll (whole scene, no per-frame shear)
    uv *= rot(ROLL_SPEED * iTime + sin(iTime * ROLL_W_FREQ) * ROLL_WOBBLE);
    uv *= 1.0 - kick * BEAT_PUMP;

    vec3 col = vec3(0.0);
    float colorShift = 0.5 + 0.5 * sin(iTime * PALETTE_SPEED);
    vec3 poisonGreen = GREEN, electricCyan = CYAN;
    vec3 accent = mix(poisonGreen, electricCyan, colorShift);

    vec2 pathNow = pathAt(cam);              // camera rides the tunnel axis

    // Look-around drift, HARD-CLAMPED: camera can never leave the frames,
    // whatever values you set for DRIFT_X / DRIFT_Y.
    vec2 drift = vec2(sin(iTime * DRIFT_SPEED) * DRIFT_X,
                      cos(iTime * DRIFT_SPEED * 0.77) * DRIFT_Y);
    vec2 lim   = vec2(FRAME_W * (1.0 - FRAME_VAR) - 0.20,
                      FRAME_H * (1.0 - FRAME_VAR) - 0.12);
    drift = clamp(drift, -lim, lim);

    // ===================== RECTANGLE FRAME LAYERS =====================
    for (float k = NUM_FRAMES; k >= 1.0; k--) {
        float m    = gb + k;                 // stable integer world id
        float dist = (k - fr) * SPACING;     // exact world distance ahead

        vec2  C  = pathAt(m * SPACING) - pathNow;
        float kk = FOCAL / (0.07 + dist * PERSP);
        vec2  p  = uv / kk - C + drift;      // frame-local coordinates

        float sz  = 1.0 - FRAME_VAR * hash(m * 0.733 + 2.1);
        vec2  box = vec2(FRAME_W, FRAME_H) * sz;

        float d  = sdFrame(p, box, FRAME_THICK);
        float dR = sdFrame(p * (1.0 - CHROMA), box, FRAME_THICK);
        float dB = sdFrame(p * (1.0 + CHROMA), box, FRAME_THICK);

        float aa = fwidth(d) + 0.0012;       // screen-space AA, no shimmer

        vec3  coreRGB = vec3(smoothstep(aa, -aa, dR),
                             smoothstep(aa, -aa, d),
                             smoothstep(aa, -aa, dB));
        float core = coreRGB.g;
        float halo = 0.016 / (abs(d) + 0.022);

        vec2  cm = smoothstep(box - 0.34, box - 0.12, abs(p));
        float corners = cm.x * cm.y * smoothstep(aa + 0.05, -aa, d);

        float pulse = (0.55 + PULSE_AMP * pow(0.5 + 0.5 * sin(m * 0.75 + iTime * PULSE_SPEED), 3.0))
                    * (0.85 + kick * 0.4);

        float ang   = atan(p.y, p.x);
        float ticks = smoothstep(0.55, 0.95, sin(ang * 13.0 + m * 1.9))
                    * smoothstep(0.05, 0.0, abs(d));

        float fog = (1.0 - smoothstep(FOG_NEAR, FOG_FAR, dist))
                  * smoothstep(0.06, 0.90, dist);       // dissolve at camera plane

        float blend      = clamp(colorShift + sin(m * 0.09) * 0.35, 0.0, 1.0);
        vec3  baseTone   = mix(poisonGreen, electricCyan, blend);
        vec3  frameColor = mix(baseTone, vec3(0.85, 1.0, 0.95), core * 0.6);

        col += frameColor
             * (coreRGB * I_CORE + halo * I_HALO + corners * I_CORNER + ticks * I_TICKS)
             * pulse * fog;
    }

    // ===================== STREAKING DUST (speed cue) =================
    float dg  = cam / DUST_SPACING;          // same grid logic, no popping
    float dfr = fract(dg), db = floor(dg);
    for (float j = DUST_LAYERS; j >= 1.0; j--) {
        float n    = db + j;
        float dist = (j - dfr) * DUST_SPACING;

        float h1 = hash(n * 1.13 + 7.7);
        float h2 = hash(n * 2.71 + 3.3);
        vec2  O   = vec2(cos(h2 * 6.2831), sin(h2 * 6.2831)) * (0.08 + h1 * 0.7);

        float k0 = FOCAL / (0.07 + dist * PERSP);               // head (now)
        float k1 = FOCAL / (0.07 + (dist + DUST_TAIL) * PERSP); // tail (a moment ago)
        vec2 a = (O - drift) * k0;
        vec2 b = (O - drift) * k1;

        vec2  pa = uv - b;
        vec2  ba = a - b;
        float h  = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-4), 0.0, 1.0);
        float dl = length(pa - ba * h);

        float dust = smoothstep(0.005, 0.0, dl)
                   * smoothstep(0.05, 0.55, dist)
                   * (1.0 - smoothstep(2.4, 3.9, dist));

        col += mix(accent, vec3(1.0), 0.4) * dust
             * (DUST_BRIGHT + 0.45 * (1.0 - dist * 0.25)) * (0.8 + kick * 0.6);
    }

    // ===================== POST =========================================
    float rays = pow(abs(sin(atan(uv.y, uv.x) * 4.0 + iTime * 0.8)), 24.0);
    col += accent * rays * smoothstep(0.08, 0.85, length(uv)) * RAYS_AMP;

    col *= (1.0 - SCAN_AMP) + SCAN_AMP * sin(fragCoord.y * 1.8 - iTime * 14.0);
    col += accent * 0.045 * smoothstep(0.65, 1.0, sin(uv.y * 1.5 - iTime * 0.9)); // refresh bar

    col *= 1.0 - VIGNETTE * dot(uv, uv);

    col = pow(max(col, 0.0), vec3(GAMMA));
    col = 1.0 - exp(-col * EXPOSURE);
    col = floor(col * POSTERIZE + ign(fragCoord.xy) * 0.5) / POSTERIZE;  // dither

    fragColor = vec4(col, 1.0);
}

// --------------------- ENTRY POINT for the screensaver ----------------
void main() {
    vec2 fragCoord = fragTexCoord.xy * iResolution.xy;  // Shadertoy-style pixel coords
    mainImage(finalColor, fragCoord);
}
