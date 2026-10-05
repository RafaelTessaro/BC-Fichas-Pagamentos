using BCFichas.Core;
using Xunit;

namespace BCFichas.Tests.Core;

public class DinheiroTests
{
    [Theory]
    [InlineData(0, "R$ 0,00")]
    [InlineData(5, "R$ 0,05")]
    [InlineData(1250, "R$ 12,50")]
    [InlineData(123456, "R$ 1.234,56")]
    [InlineData(100000000, "R$ 1.000.000,00")]
    [InlineData(-350, "-R$ 3,50")]
    public void Formata_em_reais(long centavos, string esperado) =>
        Assert.Equal(esperado, Dinheiro.Formatar(centavos));

    [Theory]
    [InlineData("12", 1200)]
    [InlineData("12,5", 1250)]
    [InlineData("12,50", 1250)]
    [InlineData("1.234,56", 123456)]
    [InlineData("R$ 3,00", 300)]
    [InlineData("12.50", 1250)]
    [InlineData("1.234", 123400)]
    [InlineData(",50", 50)]
    public void Le_valores_digitados(string texto, long esperado)
    {
        Assert.True(Dinheiro.TentarLer(texto, out var centavos));
        Assert.Equal(esperado, centavos);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12,345")]
    [InlineData("1,2,3")]
    public void Recusa_texto_invalido(string texto) => Assert.False(Dinheiro.TentarLer(texto, out _));

    [Fact]
    public void Texto_para_edicao_nao_tem_simbolo_nem_milhar() =>
        Assert.Equal("1234,50", Dinheiro.ParaEdicao(123450));
}
