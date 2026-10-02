using BCFichas.Core.Dados;
using BCFichas.Core.Impressao;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Servicos;

namespace BCFichas.Core;

/// <summary>Junta todas as peças do sistema (banco, serviços, impressora, maquininha).</summary>
public sealed class Sistema
{
    private Sistema(string pastaDados, Banco banco)
    {
        PastaDados = pastaDados;
        Banco = banco;
        Config = new ConfigServico(banco);
        Catalogo = new CatalogoServico(banco);
        Caixa = new CaixaServico(banco);
        Vendas = new VendaServico(banco);
        Impressao = new ServicoImpressao(() => Config.Atual, pastaDados);
        Maquininha = CriarMaquininha(Config.Atual);
        Config.Alterada += c =>
        {
            if (Maquininha is MaquininhaSimulada simulador && c.Maquininha == TipoMaquininha.Simulador)
                simulador.AprovarEmSegundos = c.SimuladorAprovarEmSegundos;
            else
                Maquininha = CriarMaquininha(c);
        };
    }

    public string PastaDados { get; }
    public Banco Banco { get; }
    public ConfigServico Config { get; }
    public CatalogoServico Catalogo { get; }
    public CaixaServico Caixa { get; }
    public VendaServico Vendas { get; }
    public ServicoImpressao Impressao { get; }
    public IMaquininha Maquininha { get; private set; }

    public string PastaImagens => Path.Combine(PastaDados, "imagens");

    public static Sistema Iniciar(string pastaDados, bool criarExemplos = true)
    {
        Directory.CreateDirectory(pastaDados);
        var banco = new Banco(Path.Combine(pastaDados, "bcfichas.db"));
        var sistema = new Sistema(pastaDados, banco);
        if (banco.Novo && criarExemplos && sistema.Catalogo.Abas().Count == 0)
        {
            sistema.Catalogo.CriarExemplos();
            var config = sistema.Config.Atual.Clonar();
            config.Impressora = OperatingSystem.IsWindows() ? TipoImpressora.Windows : TipoImpressora.Arquivo;
            // Primeira vez: já escolhe a Elgin se o driver dela estiver instalado.
            config.NomeImpressora = TransporteWindows.Impressoras().FirstOrDefault(i =>
                i.Contains("elgin", StringComparison.OrdinalIgnoreCase) ||
                i.Contains("i9", StringComparison.OrdinalIgnoreCase)) ?? "";
            sistema.Config.Salvar(config);
        }
        else if (sistema.Catalogo.Abas().Count == 0)
        {
            sistema.Catalogo.SalvarAba(new Aba { Nome = "ITENS" });
        }
        return sistema;
    }

    /// <summary>
    /// Pasta dos dados: "dados" ao lado do programa (fácil de copiar para backup) ou,
    /// se não der para gravar lá, a pasta do usuário.
    /// </summary>
    public static string PastaDadosPadrao()
    {
        var aoLado = Path.Combine(AppContext.BaseDirectory, "dados");
        try
        {
            Directory.CreateDirectory(aoLado);
            var teste = Path.Combine(aoLado, ".teste-escrita");
            File.WriteAllText(teste, "ok");
            File.Delete(teste);
            return aoLado;
        }
        catch (Exception)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BCFichas");
        }
    }

    private static IMaquininha CriarMaquininha(Configuracao c) => c.Maquininha switch
    {
        _ => new MaquininhaSimulada(c.SimuladorAprovarEmSegundos),
    };
}
