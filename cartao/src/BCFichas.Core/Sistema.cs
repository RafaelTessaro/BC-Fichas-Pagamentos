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
        Devolucoes = new DevolucaoServico(banco, Caixa, Vendas);
        Impressao = new ServicoImpressao(() => Config.Atual, pastaDados);
        Programacao = new ProgramacaoServico(banco, Config, Catalogo, pastaDados);
        Maquininha = CriarMaquininha(Config.Atual);
        Config.Alterada += c =>
        {
            var antiga = Maquininha;
            if (antiga is MaquininhaSimulada simulador && c.Maquininha == TipoMaquininha.Simulador)
            {
                simulador.AprovarEmSegundos = c.SimuladorAprovarEmSegundos;
                return;
            }
            // Só troca quando muda alguma coisa dela: uma maquininha nova não conhece a cobrança que está esperando o
            // operador, e a Smart 2 ficaria religando o Bluetooth a cada configuração salva
            if (antiga is MaquininhaSeparada && c.Maquininha == TipoMaquininha.Separada) return;
            if (antiga is MaquininhaPagBank smart && c.Maquininha == TipoMaquininha.PagBankSmart &&
                MesmaSmart(smart, c)) return;
            Maquininha = CriarMaquininha(c);
            (antiga as IDisposable)?.Dispose();
        };
    }

    public string PastaDados { get; }
    public Banco Banco { get; }
    public ConfigServico Config { get; }
    public CatalogoServico Catalogo { get; }
    public CaixaServico Caixa { get; }
    public VendaServico Vendas { get; }

    /// <summary>
    /// Imprime as fichas de um pedido. Se a impressão parar no meio (porta COM sem papel), grava no pedido até que
    /// ficha já saiu: a próxima impressão dele (mesmo depois de fechar o programa) manda só as que faltam.
    /// </summary>
    public ResultadoImpressao ImprimirFichas(Pedido pedido, IReadOnlyList<Ficha> fichas) =>
        Impressao.Fichas(fichas, pedido.FichasSaidas, ate =>
        {
            pedido.FichasSaidas = ate;
            try
            {
                Vendas.RegistrarFichasSaidas(pedido.Id, ate);
            }
            catch (Exception)
            {
                // Disco com erro: vale o que ficou no pedido em memória (a tela de pagamento tenta de novo com ele)
            }
        });
    public DevolucaoServico Devolucoes { get; }
    public ServicoImpressao Impressao { get; }
    public ProgramacaoServico Programacao { get; }
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
        // Fotos e logotipos que sobraram (trocados, de produtos excluídos) e fichas salvas em arquivo não ficam
        // acumulando na pasta de dados.
        try
        {
            sistema.Programacao.LimparImagensSemUso();
            sistema.Programacao.LimparFichasSalvasAntigas();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Sem permissão na pasta: o programa abre do mesmo jeito.
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
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BCFichasCartao");
        }
    }

    private static IMaquininha CriarMaquininha(Configuracao c) => c.Maquininha switch
    {
        TipoMaquininha.Simulador => new MaquininhaSimulada(c.SimuladorAprovarEmSegundos),
        TipoMaquininha.PagBankSmart => new MaquininhaPagBank(ConexaoPonte.Criar(c.MaquininhaLigacao), c.NumeroCaixa,
            c.MaquininhaComprovante),
        _ => new MaquininhaSeparada(),
    };

    private static bool MesmaSmart(MaquininhaPagBank smart, Configuracao c) =>
        smart.Caixa == c.NumeroCaixa && smart.Comprovante == c.MaquininhaComprovante &&
        smart.Conexao?.Descricao == ConexaoPonte.Criar(c.MaquininhaLigacao)?.Descricao;
}
