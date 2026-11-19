namespace WinGLSLScrSaver;

using System.Text.RegularExpressions;

internal static class ShaderAdapter
{
    public const string VertexShader = @"
#version 450
layout (location = 0) in vec2 aPosition;
layout (location = 0) out vec2 fragTexCoord;
void main()
{
    fragTexCoord = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}";

    public const string FragmentHeader = @"
#version 450
layout (location = 0) in vec2 fragTexCoord;

layout (binding = 0) uniform Uniforms
{
    vec4 uResolutionTime; // xyz = iResolution (z = aspect), w = iTime seconds
    vec4 uMouseInput;     // Shadertoy iMouse: xy = cursor px (bottom-left origin),
                          // zw = last click pos; w sign: >0 while held down, <0 after release
    vec4 uFrameDelta;     // x = iFrame (frames since start), y = iTimeDelta seconds
    vec4 uDateInput;      // Shadertoy iDate: (year, month, day, seconds since midnight)
};

#define iResolution uResolutionTime.xyz
#define iTime uResolutionTime.w
#define iMouse uMouseInput
#define iFrame int(uFrameDelta.x)
#define iTimeDelta uFrameDelta.y
#define iDate uDateInput

layout (location = 0) out vec4 finalColor;
";

    public const string DefaultShader = @"
#define pi 3.14159265

float getV(vec2 uv) {
    float l = length(uv) * pi * 2.0;
    float c = ceil(l);
    float t = iTime;
    float o = t * 4.0 / c - t * c * 0.001;
    float ox = sin(o);
    float oy = cos(o);
    uv = vec2(uv.x * ox - uv.y * oy, uv.x * oy + uv.y * ox);
    float theta = 0.5 * atan(uv.x, uv.y) / pi + 0.5;
    float q = mod(mod(theta * c, c), 1.0);
    return q;
}

void main()
{
    vec2 fragCoord = fragTexCoord.xy * iResolution.xy;
    float s = 5.0;
    float b = min(iResolution.x, iResolution.y) / s;
    vec2 r = iResolution.xy / b;
    float o = 0.3333;
    vec2 uv1 = (fragCoord + vec2(-o, -o)) * 2.0 / b - r;
    vec2 uv2 = (fragCoord + vec2(o, -o)) * 2.0 / b - r;
    vec2 uv3 = (fragCoord + vec2(o, o)) * 2.0 / b - r;
    vec2 uv4 = (fragCoord + vec2(-o, o)) * 2.0 / b - r;
    vec3 col = vec3((getV(uv1) + getV(uv2) + getV(uv3) + getV(uv4)) * 0.25);
    finalColor = vec4(col, 1.0);
}";

    public static string Adapt(string src)
    {
        src = Regex.Replace(src, @"^\s*#version.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*in\s+vec2\s+fragTexCoord\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*in\s+vec4\s+fragColor\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*layout\s*\(\s*location\s*=\s*\d+\s*\)\s*out\s+vec4\s+finalColor\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*out\s+vec4\s+finalColor\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*uniform\s+vec3\s+iResolution\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*uniform\s+float\s+iTime\s*;.*$", "", RegexOptions.Multiline);
        // Shadertoy inputs are provided by the Uniforms block - a user paste
        // that carries its own declarations would collide with them. Match
        // up to the semicolon only: `.*$` would eat code that shares the
        // line (single-line shaders).
        src = Regex.Replace(src, @"^\s*uniform\s+\w+\s+iMouse\s*;\s*(?://.*)?$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*uniform\s+\w+\s+iFrame\s*;\s*(?://.*)?$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*uniform\s+\w+\s+iTimeDelta\s*;\s*(?://.*)?$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*uniform\s+\w+\s+iDate\s*;\s*(?://.*)?$", "", RegexOptions.Multiline);
        // Sampler uniforms are stripped here and re-emitted with explicit
        // bindings (TextureDeclarations) so SPIRV-Cross maps them to known
        // registers.
        src = Regex.Replace(src, @"^\s*uniform\s+sampler2D\s+\w+\s*;.*$", "", RegexOptions.Multiline);

        Match mainMatch = Regex.Match(src, @"void\s+main\s*\(\s*\)");
        if (!mainMatch.Success && Regex.IsMatch(src, @"\bmainImage\s*\("))
        {
            src += "\nvoid main()\n{\n    vec2 fragCoord = fragTexCoord.xy * iResolution.xy;\n    mainImage(finalColor, fragCoord);\n}\n";
            mainMatch = Regex.Match(src, @"void\s+main\s*\(\s*\)");
        }
        if (mainMatch.Success)
        {
            Match m = Regex.Match(src.Substring(0, mainMatch.Index), @"vec2\s+fragCoord\s*=\s*([^;]+);");
            if (m.Success)
            {
                string expr = m.Groups[1].Value;
                src = Regex.Replace(src, @"^\s*vec2\s+fragCoord\s*=\s*[^;]+;\s*(?://.*)?\s*$", "", RegexOptions.Multiline);
                src = Regex.Replace(src, @"void\s+main\s*\(\s*\)\s*\{", "void main()\n{\n    vec2 fragCoord = " + expr + ";",
                    RegexOptions.Multiline, TimeSpan.FromSeconds(2));
            }
        }

        return src;
    }

    // ---- texture declarations ---------------------------------------------
    // Declared samplers are re-emitted with bindings 1..N; binding 0 is the
    // uResolutionTime UBO. The blit shader's t0/t1 run in separate passes and
    // are re-bound per frame, so 1..N never collide.
    public static List<string> FindSamplerNames(string src)
    {
        var names = new List<string>();
        foreach (Match m in Regex.Matches(src, @"uniform\s+sampler2D\s+(\w+)\s*;"))
            if (!names.Contains(m.Groups[1].Value))
                names.Add(m.Groups[1].Value);
        return names;
    }

    // Shadertoy compatibility: any iChannelN the user referenced but did not
    // declare is added here and bound from iChannelN.png.
    public static List<string> AllSamplerNames(string src)
    {
        var names = FindSamplerNames(src);
        foreach (Match m in Regex.Matches(src, @"\biChannel([0-3])\b"))
        {
            string name = "iChannel" + m.Groups[1].Value;
            if (!names.Contains(name)) names.Add(name);
        }
        return names;
    }

    public static string TextureDeclarations(List<string> names)
    {
        if (names.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("// texture bindings generated by the saver (PNG next to the shader file;");
        sb.AppendLine("// Shadertoy-style iChannel0..3 are auto-declared and load iChannelN.png)");
        for (int i = 0; i < names.Count; i++)
            sb.AppendLine($"layout (binding = {i + 1}) uniform sampler2D {names[i]};");
        return sb.ToString();
    }

    // The one place that builds the final fragment source: uniform header +
    // texture declarations + adapted user code. Used by startup, the editor
    // and live recompiles - they all see the same source layout.
    public static string ComposeFragment(string userSrc) =>
        FragmentHeader + TextureDeclarations(AllSamplerNames(userSrc)) + Adapt(userSrc);
}
