using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BCFichas.App.ViewModels;
using BCFichas.App.Views;
using BCFichas.Core;
using BCFichas.Tests.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>Abre a janela do programa sem tela física, com um banco temporário.</summary>
public sealed class TelaDeTeste : IDisposable
{
    public static readonly string PastaFotos = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "saida", "telas"));

    private readonly SistemaTemporario _temporario;

    /// <param name="catalogoPadrao">Usa os produtos de exemplo do programa em vez do catálogo dos testes.</param>
    public TelaDeTeste(int largura = 1280, int altura = 800, Action<Configuracao>? configurar = null,
        bool catalogoPadrao = false)
    {
        _temporario = new SistemaTemporario(exemplos: !catalogoPadrao);
        if (catalogoPadrao) Sistema.Catalogo.CriarExemplos();
        var config = Sistema.Config.Atual.Clonar();
        config.NomeEvento = "FESTA DE SÃO JOÃO";
        config.TelaCheia = false;
        configurar?.Invoke(config);
        Sistema.Config.Salvar(config);

        Principal = new PrincipalViewModel(Sistema);
        Janela = new JanelaPrincipal { DataContext = Principal, Width = largura, Height = altura };
        Janela.Show();
        Principal.Iniciar();
        Atualizar();
    }

    public Sistema Sistema => _temporario.Sistema;
    public PrincipalViewModel Principal { get; }
    public JanelaPrincipal Janela { get; }
    public string PastaImpressoes => _temporario.PastaImpressoes;

    public void AbrirCaixa(long troco = 5000)
    {
        var abertura = Assert.IsType<AberturaViewModel>(Principal.Pagina);
        abertura.Troco.Centavos = troco;
        abertura.AbrirCaixaCommand.Execute(null);
        // A abertura imprime um comprovante: espera ele sair e limpa a pasta para o teste contar só as fichas.
        if (Sistema.Config.Atual.Impressora == TipoImpressora.Arquivo)
        {
            EsperarImpressoes(1);
            foreach (var arquivo in Directory.GetFiles(PastaImpressoes, "*.png")) File.Delete(arquivo);
        }
        Principal.Aviso = null; // some com o aviso "caixa aberto" para as fotos ficarem limpas
        Atualizar();
    }

    /// <summary>Espera a impressão (que roda fora da tela) gravar os arquivos na pasta.</summary>
    public string[] EsperarImpressoes(int quantidade, int milissegundos = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(milissegundos);
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            var arquivos = Directory.Exists(PastaImpressoes) ? Directory.GetFiles(PastaImpressoes, "*.png") : [];
            if (arquivos.Length >= quantidade) return arquivos;
            if (DateTime.UtcNow > limite)
                throw new TimeoutException($"Esperava {quantidade} impressão(ões), saíram {arquivos.Length}.");
            Thread.Sleep(15);
        }
    }

    public VendaViewModel Venda => Assert.IsType<VendaViewModel>(Principal.Pagina);

    public void Tocar(string produto)
    {
        var botao = Venda.Botoes.First(b => b.Nome == produto);
        Venda.AdicionarCommand.Execute(botao);
        Atualizar();
    }

    public static void Atualizar()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Espera uma condição sem travar a fila de tarefas da tela.</summary>
    public static async Task Esperar(Func<bool> condicao, int milissegundos = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(milissegundos);
        while (!condicao())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException("A condição não aconteceu a tempo.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(15);
        }
        Atualizar();
    }

    public string Foto(string nome)
    {
        Atualizar();
        Directory.CreateDirectory(PastaFotos);
        var caminho = Path.Combine(PastaFotos, nome + ".png");
        var quadro = Janela.CaptureRenderedFrame() ?? throw new InvalidOperationException("Sem quadro renderizado.");
        quadro.Save(caminho);
        return caminho;
    }

    public T Achar<T>(Func<T, bool>? filtro = null) where T : Control =>
        Janela.GetVisualDescendants().OfType<T>().First(c => c.IsEffectivelyVisible && (filtro?.Invoke(c) ?? true));

    public void Dispose()
    {
        Principal.Dispose();
        Janela.Close();
        Dispatcher.UIThread.RunJobs();
        _temporario.Dispose();
    }
}
