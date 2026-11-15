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
};

#define iResolution uResolutionTime.xyz
#define iTime uResolutionTime.w

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
}
