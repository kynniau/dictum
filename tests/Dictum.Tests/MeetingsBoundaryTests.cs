using System.Reflection;
using System.Reflection.Emit;
using Dictum.Meetings;
using Dictum.Meetings.Store;

namespace Dictum.Tests;

public class MeetingsBoundaryTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(c => c.Value);

    [Fact]
    public void Nothing_outside_Dictum_Meetings_writes_transcript_or_audio_bytes()
    {
        var violations = new List<string>();
        foreach (var assembly in new[] { typeof(FileMeetingStore).Assembly, typeof(Dictum.UI.App).Assembly })
        foreach (var type in assembly.GetTypes().Where(t => t.Namespace != "Dictum.Meetings" && !(t.Namespace?.StartsWith("Dictum.Meetings.", StringComparison.Ordinal) ?? false)))
        {
            foreach (var field in type.GetFields(Declared))
                if (ReferencesStore(field.FieldType)) violations.Add($"{type.FullName}.{field.Name}: store field");
            foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                if (method.GetParameters().Any(p => ReferencesStore(p.ParameterType)) || method is MethodInfo info && ReferencesStore(info.ReturnType))
                    violations.Add($"{type.FullName}.{method.Name}: store signature");
                foreach (var operand in Operands(method))
                {
                    if (operand is Type referenced && ReferencesStore(referenced) || operand is MemberInfo member && member.DeclaringType is { } declaring && ReferencesStore(declaring))
                        violations.Add($"{type.FullName}.{method.Name}: store reference");
                    // Stronger than a write-only check: any encrypted-meeting filename literal outside
                    // this namespace is rejected, including compiler-generated async/lambda methods.
                    if (operand is string text && text.Contains(".enc", StringComparison.OrdinalIgnoreCase))
                        violations.Add($"{type.FullName}.{method.Name}: encrypted filename");
                }
            }
        }
        Assert.Empty(violations);
    }

    private static bool ReferencesStore(Type type) => type == typeof(FileMeetingStore)
        || type.HasElementType && ReferencesStore(type.GetElementType()!)
        || type.IsGenericType && type.GetGenericArguments().Any(ReferencesStore);

    private static IEnumerable<object> Operands(MethodBase method)
    {
        var body = method.GetMethodBody();
        if (body is null) yield break;
        foreach (var local in body.LocalVariables) yield return local.LocalType;
        var bytes = body.GetILAsByteArray()!;
        var typeArgs = method.DeclaringType?.GetGenericArguments();
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var at = 0; at < bytes.Length;)
        {
            short code = bytes[at++];
            if (code == 0xFE) code = (short)(0xFE00 | bytes[at++]);
            var op = Codes[code];
            var size = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, at) * 4,
                _ => 4
            };
            if (op.OperandType == OperandType.InlineString) yield return method.Module.ResolveString(BitConverter.ToInt32(bytes, at));
            else if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var member = method.Module.ResolveMember(BitConverter.ToInt32(bytes, at), typeArgs, methodArgs);
                if (member is not null) yield return member;
            }
            at += size;
        }
    }
}
