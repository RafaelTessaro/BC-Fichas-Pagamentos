using BCFichas.Core;
using BCFichas.Core.Impressao;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// Comprovantes feitos no modo teste (sangria, suprimento, devolução, parcial, fechamento) saem marcados
/// "TESTE • SEM VALOR", em cima e embaixo, como as fichas de teste. Os de verdade não mudam.
/// </summary>
public sealed class ComprovanteDeTesteTests : IDisposable
{
    private readonly SistemaTemporario _t = new();
    private Sistema S => _t.Sistema;
    public void Dispose() => _t.Dispose();

    private static readonly string Pasta =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "saida", "fichas"));

    [Fact]
    public void Comprovantes_do_modo_teste_saem_marcados_e_os_de_verdade_nao_mudam()
    {
        var real = S.Caixa.Abrir(1, null, 5000);
        var sangriaReal = S.Caixa.RegistrarMovimento(real, TipoMovimento.Sangria, 2000, "");
        using var comprovanteReal = Relatorios.Movimento(sangriaReal, S.Config.Atual, 3000).Renderizar(576);
        using var comprovanteRealDeNovo = Relatorios.Movimento(sangriaReal, S.Config.Atual, 3000, teste: false).Renderizar(576);
        using var comprovanteMarcado = Relatorios.Movimento(sangriaReal, S.Config.Atual, 3000, teste: true).Renderizar(576);
        Assert.Equal(comprovanteReal.Height, comprovanteRealDeNovo.Height);
        Assert.True(comprovanteMarcado.Height > comprovanteReal.Height + 40, "o comprovante de teste não tem as duas faixas");

        var teste = S.Caixa.Abrir(1, null, 0, teste: true);
        var devolucaoTeste = S.Caixa.RegistrarMovimento(teste, TipoMovimento.Suprimento, 50000, "");
        using var desenho = Relatorios.Movimento(devolucaoTeste, S.Config.Atual, 50000, teste.Teste).Renderizar(576);
        Directory.CreateDirectory(Pasta);
        using (var mono = ImagemUtil.Monocromatico(desenho)) ImagemUtil.SalvarPng(mono, Path.Combine(Pasta, "comprovante-de-teste.png"));

        // A mesma parcial, do caixa de teste e como se fosse de verdade: só as duas faixas de diferença
        var resumo = S.Caixa.Resumo(teste.Id);
        Assert.True(resumo.Sessao.Teste);
        using var parcialTeste = Relatorios.Fechamento(resumo, S.Config.Atual, parcial: true).Renderizar(576);
        resumo.Sessao.Teste = false;
        using var parcialNormal = Relatorios.Fechamento(resumo, S.Config.Atual, parcial: true).Renderizar(576);
        Assert.True(parcialTeste.Height > parcialNormal.Height + 40, "a parcial do modo teste não está marcada");
    }
}
