using System.Security.Cryptography;
using System.Text;

namespace BCFichas.Core;

/// <summary>
/// Senha técnica da BC Fichas: abre a aba Máquina das Configurações (programação, backup, opções liberadas para o
/// cliente). É fixa, a mesma em todas as máquinas, e não depende da senha master do cliente. O código é público,
/// então aqui fica só o resumo (PBKDF2) da senha, nunca a senha.
/// </summary>
public sealed class SenhaTecnica
{
    private const int Iteracoes = 60_000;
    private static readonly byte[] SalPadrao = Convert.FromHexString("7b8a16ca60db69463a996ada81e479ad");
    private static readonly byte[] ResumoPadrao =
        Convert.FromHexString("d12500f2ea984ebe26386bfe96c2142efc4912643d9762849fd9d83cfefe25b2");

    private readonly byte[] _sal;
    private readonly byte[] _resumo;
    private readonly int _iteracoes;

    /// <summary>A senha técnica da BC Fichas.</summary>
    public static SenhaTecnica Padrao { get; } = new(SalPadrao, ResumoPadrao, Iteracoes);

    public SenhaTecnica(byte[] sal, byte[] resumo, int iteracoes)
    {
        _sal = sal;
        _resumo = resumo;
        _iteracoes = iteracoes;
    }

    /// <summary>Confere a senha digitada (sem diferença entre maiúsculas e minúsculas, sem espaços nas pontas).</summary>
    public bool Confere(string? digitada)
    {
        var texto = (digitada ?? "").Trim().ToLowerInvariant();
        if (texto.Length == 0) return false;
        var resumo = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(texto), _sal, _iteracoes, HashAlgorithmName.SHA256,
            _resumo.Length);
        return CryptographicOperations.FixedTimeEquals(resumo, _resumo);
    }
}
