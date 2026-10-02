namespace BCFichas.Core.Impressao;

/// <summary>Gera as barras de um código Code 128 (conjunto B, ou C quando é só número com tamanho par).</summary>
public static class Code128
{
    // Padrões de 11 módulos (barra/espaço alternados), índice = valor do símbolo. 106 = STOP (13 módulos).
    private static readonly string[] Padroes =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    private const int StartB = 104;
    private const int StartC = 105;
    private const int Stop = 106;

    /// <summary>
    /// Retorna a sequência de módulos: true = barra preta. Inclui zona de silêncio de 10 módulos de cada lado.
    /// </summary>
    public static bool[] Modulos(string texto)
    {
        if (string.IsNullOrEmpty(texto)) throw new ArgumentException("Texto vazio.", nameof(texto));

        var valores = new List<int>();
        if (texto.Length % 2 == 0 && texto.All(char.IsAsciiDigit))
        {
            valores.Add(StartC);
            for (var i = 0; i < texto.Length; i += 2)
                valores.Add((texto[i] - '0') * 10 + (texto[i + 1] - '0'));
        }
        else
        {
            valores.Add(StartB);
            foreach (var ch in texto)
            {
                if (ch < 32 || ch > 126) throw new ArgumentException($"Caractere inválido para Code 128: '{ch}'.");
                valores.Add(ch - 32);
            }
        }

        var soma = valores[0];
        for (var i = 1; i < valores.Count; i++) soma += valores[i] * i;
        valores.Add(soma % 103);
        valores.Add(Stop);

        var modulos = new List<bool>();
        modulos.AddRange(Enumerable.Repeat(false, 10));
        foreach (var valor in valores)
        {
            var padrao = Padroes[valor];
            for (var i = 0; i < padrao.Length; i++)
            {
                var largura = padrao[i] - '0';
                var barra = i % 2 == 0;
                for (var k = 0; k < largura; k++) modulos.Add(barra);
            }
        }
        modulos.AddRange(Enumerable.Repeat(false, 10));
        return modulos.ToArray();
    }
}
