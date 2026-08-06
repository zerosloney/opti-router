using System.Text.Json;

namespace OptiRouter.Clients;

/// <summary>
/// JsonNamingPolicy，将 PascalCase 属性名转换为 snake_case。
/// 仅用于 OpenAI 响应反序列化，请求序列化仍保持 camelCase。
/// </summary>
internal sealed class JsonSnakeCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var sb = new System.Text.StringBuilder(name.Length + 5);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
