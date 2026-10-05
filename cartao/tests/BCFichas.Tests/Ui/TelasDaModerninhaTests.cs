using Avalonia.Headless.XUnit;
using BCFichas.App.ViewModels;
using BCFichas.Core;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Servicos;
using BCFichas.Core.Vendas;
using BCFichas.Tests.Core;
using Xunit;

namespace BCFichas.Tests.Ui;

/// <summary>
/// BC Fichas Cartão: a Moderninha Smart 2 ligada por Bluetooth. Nas telas, a maquininha é a <see cref="PonteFalsa"/>
/// (o endereço Bluetooth escolhido na tela é desviado para ela: o computador dos testes não tem Bluetooth).
/// </summary>
public class TelasDaModerninhaTests : IDisposable
{
    private const int AbaMaquininha = 4;
    private const string Endereco = "00:1A:7D:DA:71:13";
    private readonly PonteFalsa _ponte = new();

    public TelasDaModerninhaTests()
    {
        TemposMaquininha.Padrao = new TemposMaquininha(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        ConexaoPonte.Desvio = texto => texto == Endereco ? new ConexaoTcp("127.0.0.1", _ponte.Porta) : null;
        ConfiguracaoViewModel.ListarAparelhos = () =>
        [
            new AparelhoBluetooth("Fone JBL", 0x112233445566, false),
            new AparelhoBluetooth("PagBank P2 PB0123456", 0x001A7DDA7113, true),
        ];
    }

    public void Dispose()
    {
        TemposMaquininha.Padrao = TemposMaquininha.Reais;
        ConexaoPonte.Desvio = null;
        ConfiguracaoViewModel.ListarAparelhos = BluetoothWindows.Pareados;
        _ponte.Dispose();
    }

    private static TelaDeTeste Tela() => new(configurar: c =>
    {
        c.Maquininha = TipoMaquininha.PagBankSmart;
        c.MaquininhaLigacao = Endereco;
        c.MaquininhaNome = "PagBank P2 PB0123456";
    });

    private static PagamentoViewModel Pagar(TelaDeTeste t, params string[] produtos)
    {
        foreach (var p in produtos) t.Tocar(p);
        t.Venda.PagarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        return Assert.IsType<PagamentoViewModel>(t.Principal.Dialogo);
    }

    private static Pedido UltimoPedido(TelaDeTeste t) =>
        t.Sistema.Vendas.Pedido(t.Sistema.Vendas.Pedidos(t.Principal.Sessao!.Id, limite: 1).Single().Id)!;

    [AvaloniaFact]
    public async Task Configura_a_Smart_2_escolhendo_a_maquininha_pareada_e_testando()
    {
        using var t = new TelaDeTeste();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.AbaSelecionada = AbaMaquininha;
        tela.EscolherMaquininhaCommand.Execute(TipoMaquininha.PagBankSmart);
        TelaDeTeste.Atualizar();
        Assert.True(tela.MaquininhaSmart);
        Assert.False(tela.MaquininhaSeparada);

        // A maquininha vem primeiro na lista; escolher preenche o endereço
        Assert.Equal(["PagBank P2 PB0123456", "Fone JBL"], tela.Aparelhos.Select(a => a.Nome));
        tela.AparelhoEscolhido = tela.Aparelhos[0];
        Assert.Equal(Endereco, tela.MaquininhaLigacao);

        await tela.TestarMaquininhaCommand.ExecuteAsync(null);
        Assert.True(tela.TesteOk, tela.ResultadoTeste);
        Assert.Equal("Ligou! PagBank Moderninha Smart 2 (nº de série PB0123456) pronta para cobrar.", tela.ResultadoTeste);
        // Rola até a maquininha escolhida e o resultado do teste aparecerem juntos
        TelaDeTeste.Atualizar();
        var rolagem = t.Achar<Avalonia.Controls.ScrollViewer>(r => r.Extent.Height > r.Viewport.Height + 50);
        rolagem.Offset = new Avalonia.Vector(0, 180);
        t.Foto("80-config-maquininha-smart");

        tela.SalvarCommand.Execute(null);
        TelaDeTeste.Atualizar();
        var c = t.Sistema.Config.Atual;
        Assert.Equal((TipoMaquininha.PagBankSmart, Endereco, "PagBank P2 PB0123456", false),
            (c.Maquininha, c.MaquininhaLigacao, c.MaquininhaNome, c.MaquininhaComprovante));
        Assert.IsType<MaquininhaPagBank>(t.Sistema.Maquininha);

        // Abrir de novo: a maquininha escolhida continua escolhida
        var outra = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(outra);
        Assert.Equal("PagBank P2 PB0123456", outra.AparelhoEscolhido?.Nome);
        Assert.False(outra.TemAlteracoes);
    }

    [AvaloniaFact]
    public async Task Teste_com_a_maquininha_desligada_ou_sem_escolher_avisa()
    {
        using var t = new TelaDeTeste();
        var tela = new ConfiguracaoViewModel(t.Principal);
        t.Principal.Abrir(tela);
        tela.EscolherMaquininhaCommand.Execute(TipoMaquininha.PagBankSmart);

        // Smart 2 sem maquininha escolhida: não salva (toda venda em cartão daria erro)
        tela.SalvarCommand.Execute(null);
        Assert.Equal("Escolha a Moderninha Smart 2 na lista da aba Maquininha.", t.Principal.Aviso);
        Assert.Equal(AbaMaquininha, tela.AbaSelecionada);
        Assert.Equal(TipoMaquininha.Separada, t.Sistema.Config.Atual.Maquininha);
        await tela.TestarMaquininhaCommand.ExecuteAsync(null);
        Assert.True(tela.TesteFalhou);

        _ponte.Desligada = true;
        tela.MaquininhaLigacao = Endereco;
        await tela.TestarMaquininhaCommand.ExecuteAsync(null);
        Assert.True(tela.TesteFalhou);
        Assert.StartsWith("Não consegui ligar na maquininha", tela.ResultadoTeste);
    }

    [AvaloniaFact]
    public async Task Venda_no_credito_vai_sozinha_para_a_maquininha_e_imprime_quando_aprovar()
    {
        using var t = Tela();
        t.AbrirCaixa();
        _ponte.DecidirSozinhaEm = null;
        var pagamento = Pagar(t, "PASTEL", "PASTEL", "ESPETINHO");
        Assert.True(pagamento.EhIntegrada);
        Assert.False(pagamento.EhSimulador);
        var credito = pagamento.CreditoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => _ponte.Cobrancas == 1 && pagamento.Andamento == "Aproxime, insira ou passe o cartão");
        Assert.True(pagamento.EmMaquininha);
        var cobrar = _ponte.Recebidas.Single(m => m.Tipo == "cobrar");
        var pedidoNovo = UltimoPedido(t);
        Assert.Equal((3200L, "credito", VendaServico.IdCobranca(pedidoNovo), VendaServico.ReferenciaCobranca(pedidoNovo)),
            (cobrar.Valor!.Value, cobrar.Forma, cobrar.Id, cobrar.Referencia));
        Assert.Matches("^P0001[A-Z0-9]{5}$", cobrar.Referencia);
        t.Foto("81-pagamento-smart-esperando");

        _ponte.Concluir(aprovado: true);
        await credito;
        Assert.True(pagamento.EmConcluido);
        Assert.Equal(3, t.EsperarImpressoes(3).Length);
        var pedido = UltimoPedido(t);
        Assert.Equal(StatusPedido.Pago, pedido.Status);
        Assert.Equal("VISA • aut 123456 • NSU 000987 • cód ABC123XYZ", pedido.Autorizacao);
    }

    [AvaloniaFact]
    public async Task Maquininha_que_some_no_meio_deixa_o_pedido_guardado_e_o_operador_consulta_de_novo()
    {
        using var t = Tela();
        t.AbrirCaixa();
        _ponte.DecidirSozinhaEm = null;
        var pagamento = Pagar(t, "PASTEL");
        var debito = pagamento.DebitoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => _ponte.Cobrancas == 1);
        // A maquininha sai do alcance (o cliente paga nela mesmo assim)
        _ponte.Desligada = true;
        _ponte.DerrubarLigacao();
        await debito.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(pagamento.EmSemResposta);
        Assert.False(pagamento.PodeFechar);
        Assert.Equal(StatusPedido.AguardandoPagamento, UltimoPedido(t).Status);
        TelaDeTeste.Atualizar();
        t.Foto("82-pagamento-smart-sem-resposta");

        // Ainda longe: consultar de novo continua esperando, sem cancelar nada
        await pagamento.ConsultarDeNovoCommand.ExecuteAsync(null);
        Assert.True(pagamento.EmSemResposta);

        // Voltou: o cliente tinha pago
        _ponte.Concluir(aprovado: true);
        _ponte.Desligada = false;
        await pagamento.ConsultarDeNovoCommand.ExecuteAsync(null);
        Assert.True(pagamento.EmConcluido);
        Assert.Single(t.EsperarImpressoes(1));
        Assert.Equal(StatusPedido.Pago, UltimoPedido(t).Status);
        Assert.Equal(1, _ponte.Cobrancas);
    }

    [AvaloniaFact]
    public async Task Sem_resposta_o_operador_confere_na_maquininha_e_diz_que_nao_foi_pago()
    {
        using var t = Tela();
        t.AbrirCaixa();
        _ponte.DecidirSozinhaEm = null;
        var pagamento = Pagar(t, "PASTEL");
        var pix = pagamento.PixCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => _ponte.Cobrancas == 1);
        Assert.Equal("pix", _ponte.Recebidas.Single(m => m.Tipo == "cobrar").Forma);
        _ponte.Desligada = true;
        _ponte.DerrubarLigacao();
        await pix.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(pagamento.EmSemResposta);

        // "Não foi pago" pede confirmação; voltar não cancela nada
        var naoPago = pagamento.NaoFoiPagoCommand.ExecuteAsync(null);
        var pergunta = Assert.IsType<MensagemViewModel>(t.Principal.Dialogo);
        Assert.True(pergunta.Perigo);
        pergunta.NaoCommand.Execute(null);
        await naoPago;
        Assert.True(pagamento.EmSemResposta);

        naoPago = pagamento.NaoFoiPagoCommand.ExecuteAsync(null);
        Assert.IsType<MensagemViewModel>(t.Principal.Dialogo).SimCommand.Execute(null);
        await naoPago;
        Assert.True(pagamento.EmRecusado);
        Assert.Equal(StatusPedido.Cancelado, UltimoPedido(t).Status);
        Assert.False(t.Venda.Vazio); // o pedido continua na tela para cobrar de outro jeito
    }

    [AvaloniaFact]
    public async Task Toques_seguidos_na_tela_sem_resposta_imprimem_as_fichas_uma_vez_so()
    {
        using var t = Tela();
        t.AbrirCaixa();
        _ponte.DecidirSozinhaEm = null;
        var pagamento = Pagar(t, "PASTEL");
        var debito = pagamento.DebitoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => _ponte.Cobrancas == 1);
        _ponte.Desligada = true;
        _ponte.DerrubarLigacao();
        await debito.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(pagamento.EmSemResposta);
        _ponte.Concluir(aprovado: true);
        _ponte.Desligada = false;

        // "Aprovou na maquininha" com a confirmação aberta: outro toque (nele ou em Consultar) não faz nada
        var aprovou = pagamento.FoiAprovadoCommand.ExecuteAsync(null);
        var pergunta = Assert.IsType<MensagemViewModel>(t.Principal.Dialogo);
        var consultasAntes = _ponte.Recebidas.Count(m => m.Tipo == "consultar");
        await pagamento.ConsultarDeNovoCommand.ExecuteAsync(null);
        await pagamento.NaoFoiPagoCommand.ExecuteAsync(null);
        Assert.Same(pergunta, t.Principal.Dialogo);
        Assert.Equal(consultasAntes, _ponte.Recebidas.Count(m => m.Tipo == "consultar"));

        pergunta.SimCommand.Execute(null);
        await aprovou;
        Assert.True(pagamento.EmConcluido);
        Assert.Single(t.EsperarImpressoes(1));
        await Task.Delay(300);
        Assert.Single(t.EsperarImpressoes(1));
        Assert.Equal(StatusPedido.Pago, UltimoPedido(t).Status);
    }

    [AvaloniaFact]
    public async Task Cancelar_tocado_enquanto_o_pedido_e_gravado_nem_manda_para_a_maquininha()
    {
        using var t = Tela();
        t.AbrirCaixa();
        var pagamento = Pagar(t, "PASTEL");
        // O toque em Cancelar chega antes de o pedido terminar de ser gravado (disco lento do tablet)
        var credito = pagamento.CreditoCommand.ExecuteAsync(null);
        pagamento.CancelarMaquininhaCommand.Execute(null);
        await credito.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(pagamento.EmEscolher);
        Assert.Equal(0, _ponte.Cobrancas);
        Assert.Equal(StatusPedido.Cancelado, UltimoPedido(t).Status);
    }

    [AvaloniaFact]
    public async Task Aprovado_depois_de_o_operador_dizer_que_nao_foi_pago_mostra_um_aviso()
    {
        using var t = Tela();
        t.AbrirCaixa();
        _ponte.DecidirSozinhaEm = null;
        var pagamento = Pagar(t, "PASTEL");
        var credito = pagamento.CreditoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => _ponte.Cobrancas == 1);
        _ponte.Muda = true;
        await credito.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(pagamento.EmSemResposta);
        var desistiu = pagamento.NaoFoiPagoCommand.ExecuteAsync(null);
        Assert.IsType<MensagemViewModel>(t.Principal.Dialogo).SimCommand.Execute(null);
        await desistiu;
        var cancelado = UltimoPedido(t);
        Assert.Equal(StatusPedido.Cancelado, cancelado.Status);
        pagamento.FecharCommand.Execute(null);

        // O cliente pagou mesmo assim: a maquininha avisa quando volta, na venda seguinte
        _ponte.Muda = false;
        _ponte.Concluir(aprovado: true);
        _ponte.DecidirSozinhaEm = TimeSpan.FromMilliseconds(100);
        var seguinte = Pagar(t, "PASTEL");
        await seguinte.DebitoCommand.ExecuteAsync(null);
        Assert.True(seguinte.EmConcluido);
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel, 10000);
        var aviso = (MensagemViewModel)t.Principal.Dialogo!;
        Assert.Contains($"aprovou o pedido {cancelado.Numero}", aviso.Texto);
        Assert.Contains("estorno", aviso.Texto);
    }

    [AvaloniaFact]
    public async Task Cancelar_no_tablet_cancela_na_maquininha_e_volta_para_as_formas()
    {
        using var t = Tela();
        t.AbrirCaixa();
        _ponte.DecidirSozinhaEm = null;
        var pagamento = Pagar(t, "PASTEL");
        var credito = pagamento.CreditoCommand.ExecuteAsync(null);
        await TelaDeTeste.Esperar(() => _ponte.Cobrancas == 1);
        pagamento.CancelarMaquininhaCommand.Execute(null);
        await credito.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(pagamento.EmEscolher);
        Assert.Equal(1, _ponte.Cancelamentos);
        Assert.Equal(StatusPedido.Cancelado, UltimoPedido(t).Status);
    }

    [AvaloniaFact]
    public async Task Maquininha_desligada_avisa_sem_cobrar()
    {
        using var t = Tela();
        t.AbrirCaixa();
        _ponte.Desligada = true;
        var pagamento = Pagar(t, "PASTEL");
        await pagamento.CreditoCommand.ExecuteAsync(null);
        Assert.True(pagamento.EmRecusado);
        Assert.StartsWith("Não consegui ligar na maquininha por Bluetooth.", pagamento.Mensagem);
        Assert.Equal(0, _ponte.Cobrancas);
        Assert.Equal(StatusPedido.Cancelado, UltimoPedido(t).Status);
        TelaDeTeste.Atualizar();
        t.Foto("83-pagamento-smart-desligada");
    }

    [AvaloniaFact]
    public async Task Ao_abrir_o_programa_pergunta_ao_operador_o_pedido_que_a_maquininha_nao_confirmou()
    {
        using var t = Tela();
        t.AbrirCaixa();
        var carrinho = new Carrinho();
        carrinho.Adicionar(t.Sistema.Catalogo.Produtos().First(p => p.Nome == "PASTEL"));
        var pendente = t.Sistema.Vendas.CriarPedido(t.Principal.Sessao!, carrinho.Linhas, FormaPagamento.Credito);
        _ponte.Desligada = true;

        // O programa abre de novo com a maquininha desligada: pergunta, e só cancela se o operador disser
        t.Principal.Iniciar();
        await TelaDeTeste.Esperar(() => t.Principal.Dialogo is MensagemViewModel, 15000);
        var pergunta = (MensagemViewModel)t.Principal.Dialogo!;
        Assert.Contains("A maquininha não respondeu agora", pergunta.Texto);
        Assert.Equal(StatusPedido.AguardandoPagamento, t.Sistema.Vendas.Pedido(pendente.Id)!.Status);
        pergunta.SimCommand.Execute(null);
        await TelaDeTeste.Esperar(() => t.Sistema.Vendas.Pedido(pendente.Id)!.Status == StatusPedido.Pago);
        Assert.Single(t.EsperarImpressoes(1));

        // Pago na maquininha antes de o programa fechar: recupera sozinho
        var pago = t.Sistema.Vendas.CriarPedido(t.Principal.Sessao!, carrinho.Linhas, FormaPagamento.Debito);
        _ponte.Guardar(VendaServico.IdCobranca(pago), aprovado: true);
        _ponte.Desligada = false;
        t.Principal.Iniciar();
        await TelaDeTeste.Esperar(() => t.Sistema.Vendas.Pedido(pago.Id)!.Status == StatusPedido.Pago, 15000);
    }

    [Fact]
    public void Backup_da_Smart_2_abre_no_BC_Fichas_original()
    {
        using var temporario = new SistemaTemporario();
        var s = temporario.Sistema;
        var c = s.Config.Atual.Clonar();
        c.Maquininha = TipoMaquininha.PagBankSmart;
        c.MaquininhaLigacao = Endereco;
        s.Config.Salvar(c);
        var arquivo = Path.Combine(temporario.Pasta, s.Programacao.NomeArquivo());
        s.Programacao.Salvar(arquivo);

        // O BC Fichas original e o Rede só conhecem "Separada" e "Simulador"
        using var zip = System.IO.Compression.ZipFile.OpenRead(arquivo);
        using var leitor = new StreamReader(zip.Entries.First(e => e.Name.EndsWith(".json")).Open());
        var json = leitor.ReadToEnd();
        Assert.DoesNotContain("PagBankSmart", json);
        Assert.Equal(TipoMaquininha.PagBankSmart, s.Config.Atual.Maquininha);
    }
}
