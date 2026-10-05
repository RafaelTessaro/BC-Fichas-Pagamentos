using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using BCFichas.Core;
using BCFichas.Core.Pagamento;
using BCFichas.Core.Servicos;
using BCFichas.Core.Vendas;
using Xunit;

namespace BCFichas.Tests.Core;

/// <summary>
/// A Moderninha Smart 2 ligada ao tablet: o tablet manda o valor, a maquininha (aqui, a <see cref="PonteFalsa"/>)
/// cobra e responde. O que importa é o dinheiro: nunca cobrar duas vezes, nunca perder um pagamento aprovado e nunca
/// cancelar sozinho um pedido que pode ter sido pago.
/// </summary>
public class MaquininhaPagBankTests : IDisposable
{
    private readonly PonteFalsa _ponte = new();
    private readonly List<MaquininhaPagBank> _maquininhas = [];

    public void Dispose()
    {
        foreach (var m in _maquininhas) m.Dispose();
        _ponte.Dispose();
    }

    private MaquininhaPagBank Maquininha(string? ligacao = null, bool comprovante = false, int religarEm = 3)
    {
        var m = new MaquininhaPagBank(ConexaoPonte.Criar(ligacao ?? _ponte.Ligacao), 1, comprovante)
        {
            EsperaLigar = TimeSpan.FromSeconds(2),
            IntervaloPing = TimeSpan.FromMilliseconds(100),
            Silencio = TimeSpan.FromMilliseconds(800),
            EsperaReligar = TimeSpan.FromSeconds(religarEm),
            EsperaConsulta = TimeSpan.FromSeconds(2),
        };
        _maquininhas.Add(m);
        return m;
    }

    private static Cobranca Cobranca(string id = "C01-P000005-12", FormaPagamento forma = FormaPagamento.Credito,
        long valor = 2350) => new(id, valor, forma, "FESTA - pedido 5", ProtocoloPonte.Referencia(id, 5));

    private sealed class Anotador : IProgress<string>
    {
        public List<string> Textos { get; } = [];
        public void Report(string value)
        {
            lock (Textos) Textos.Add(value);
        }
    }

    [Fact]
    public async Task Cartao_aprovado_volta_com_a_autorizacao_e_o_valor_vai_certo()
    {
        var m = Maquininha(comprovante: true);
        var andamento = new Anotador();
        var r = await m.CobrarAsync(Cobranca(), andamento, CancellationToken.None);

        Assert.True(r.Aprovado);
        Assert.Equal("VISA • aut 123456 • NSU 000987 • cód ABC123XYZ", r.Autorizacao);
        Assert.Contains("Aproxime, insira ou passe o cartão", andamento.Textos);
        var cobrar = _ponte.Recebidas.Single(x => x.Tipo == "cobrar");
        Assert.Equal(("C01-P000005-12", ProtocoloPonte.Referencia("C01-P000005-12", 5), 2350L, "credito", true),
            (cobrar.Id, cobrar.Referencia, cobrar.Valor, cobrar.Forma, cobrar.Comprovante));
        var ola = _ponte.Recebidas.First();
        Assert.Equal(("ola", ProtocoloPonte.Versao, 1), (ola.Tipo, ola.Versao, ola.Caixa));
        Assert.Equal(1, _ponte.Cobrancas);
        Assert.Equal(("PagBank Moderninha Smart 2", "PB0123456", true), (m.Info!.Modelo, m.Info.Serial, m.Info.Pronta));
    }

    [Fact]
    public async Task Recusado_volta_nao_aprovado_e_a_venda_seguinte_usa_a_mesma_ligacao()
    {
        var m = Maquininha();
        _ponte.Aprovar = false;
        var r = await m.CobrarAsync(Cobranca(forma: FormaPagamento.Pix), null, CancellationToken.None);
        Assert.False(r.Aprovado);
        Assert.Equal("Cartão recusado", r.Mensagem);
        Assert.Equal("pix", _ponte.Recebidas.Single(x => x.Tipo == "cobrar").Forma);

        _ponte.Aprovar = true;
        Assert.True((await m.CobrarAsync(Cobranca("C01-P000006-13"), null, CancellationToken.None)).Aprovado);
        Assert.Equal(1, _ponte.Ligacoes);
    }

    [Fact]
    public async Task O_mesmo_pedido_enviado_de_novo_nao_e_cobrado_duas_vezes()
    {
        var m = Maquininha();
        Assert.True((await m.CobrarAsync(Cobranca(), null, CancellationToken.None)).Aprovado);
        // O tablet reenviou (ex.: travou e o operador tocou de novo): a maquininha devolve o mesmo resultado
        var outra = await m.CobrarAsync(Cobranca(), null, CancellationToken.None);
        Assert.True(outra.Aprovado);
        Assert.Equal(1, _ponte.Cobrancas);
    }

    [Fact]
    public async Task Ligacao_caiu_no_meio_o_tablet_liga_de_novo_e_recebe_o_resultado_sem_cobrar_de_novo()
    {
        var m = Maquininha();
        _ponte.CairAoReceberCobranca = true;
        _ponte.DecidirSozinhaEm = TimeSpan.FromMilliseconds(600);
        var andamento = new Anotador();
        var r = await m.CobrarAsync(Cobranca(), andamento, CancellationToken.None);

        Assert.True(r.Aprovado);
        Assert.Equal(1, _ponte.Cobrancas);
        Assert.True(_ponte.Ligacoes >= 2);
        Assert.Contains(_ponte.Recebidas, x => x.Tipo == "consultar" && x.Id == "C01-P000005-12");
        Assert.Contains("A ligação com a maquininha caiu. Ligando de novo...", andamento.Textos);
    }

    [Fact]
    public async Task Ligacao_muda_sem_fechar_e_percebida_pelo_silencio_e_religada()
    {
        var m = Maquininha(religarEm: 8);
        _ponte.DecidirSozinhaEm = null;
        var cobranca = m.CobrarAsync(Cobranca(), null, CancellationToken.None);
        await Esperar(() => _ponte.Cobrancas == 1);
        // O Bluetooth "morreu" sem avisar (a ligação nem fecha): a maquininha some, mas o cliente paga nela
        _ponte.Muda = true;
        _ponte.Concluir(aprovado: true);
        // O tablet percebe pelo silêncio (nem "pong") e tenta ligar de novo; a maquininha volta depois
        await Esperar(() => _ponte.Ligacoes >= 2);
        _ponte.Muda = false;

        var r = await cobranca.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(r.Aprovado);
        Assert.Equal(1, _ponte.Cobrancas);
    }

    [Fact]
    public async Task Cancelar_no_tablet_pede_para_a_maquininha_cancelar()
    {
        var m = Maquininha();
        _ponte.DecidirSozinhaEm = null;
        using var cancelar = new CancellationTokenSource();
        var cobranca = m.CobrarAsync(Cobranca(), null, cancelar.Token);
        await Esperar(() => _ponte.Cobrancas == 1);
        cancelar.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cobranca.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, _ponte.Cancelamentos);
    }

    [Fact]
    public async Task Cancelar_depois_de_o_cliente_pagar_vale_o_pagamento()
    {
        var m = Maquininha();
        _ponte.DecidirSozinhaEm = null;
        _ponte.AceitaCancelar = false;
        using var cancelar = new CancellationTokenSource();
        var andamento = new Anotador();
        var cobranca = m.CobrarAsync(Cobranca(), andamento, cancelar.Token);
        await Esperar(() => _ponte.Cobrancas == 1);
        cancelar.Cancel();
        await Esperar(() => _ponte.Cancelamentos == 1);
        _ponte.Concluir(aprovado: true);

        var r = await cobranca.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(r.Aprovado);
        Assert.Contains("Pedindo para a maquininha cancelar...", andamento.Textos);
    }

    [Fact]
    public async Task Maquininha_que_some_no_meio_do_pagamento_nao_cancela_o_pedido()
    {
        var m = Maquininha();
        _ponte.DecidirSozinhaEm = null;
        var cobranca = m.CobrarAsync(Cobranca(), null, CancellationToken.None);
        await Esperar(() => _ponte.Cobrancas == 1);
        _ponte.Desligada = true;
        _ponte.DerrubarLigacao();

        var relogio = Stopwatch.StartNew();
        var erro = await Assert.ThrowsAsync<MaquininhaSemResposta>(() => cobranca.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Contains("Confira nela se o pagamento foi aprovado", erro.Message);
        Assert.InRange(relogio.Elapsed.TotalSeconds, 2, 10);
    }

    [Fact]
    public async Task Sem_a_maquininha_por_perto_avisa_antes_de_mandar_a_cobranca()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var porta = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var m = Maquininha($"tcp://127.0.0.1:{porta}");
        var erro = await Assert.ThrowsAsync<ErroDeNegocio>(() => m.CobrarAsync(Cobranca(), null, CancellationToken.None));
        Assert.Contains("Não consegui ligar na maquininha", erro.Message);

        var semEscolher = new MaquininhaPagBank(null, 1, false);
        var nenhuma = await Assert.ThrowsAsync<ErroDeNegocio>(() => semEscolher.CobrarAsync(Cobranca(), null, CancellationToken.None));
        Assert.Contains("Configurações → Maquininha", nenhuma.Message);
    }

    [Fact]
    public async Task Maquininha_ocupada_ou_nao_ativada_nao_cobra()
    {
        // O programa do tablet fechou no meio de um pagamento (que continua na maquininha) e abriu de novo
        var antes = Maquininha();
        _ponte.DecidirSozinhaEm = null;
        var interrompida = antes.CobrarAsync(Cobranca(), null, CancellationToken.None);
        await Esperar(() => _ponte.Cobrancas == 1);
        antes.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => interrompida.WaitAsync(TimeSpan.FromSeconds(10)));

        // Uma venda nova enquanto a maquininha ainda espera o cliente da anterior: não cobra
        var depois = Maquininha();
        var r = await depois.CobrarAsync(Cobranca("C01-P000006-13"), null, CancellationToken.None);
        Assert.False(r.Aprovado);
        Assert.Equal("A maquininha está fazendo outro pagamento.", r.Mensagem);

        // O cliente da anterior pagou: ao consultar (o que o programa faz ao abrir), ela aparece paga
        _ponte.Concluir(true);
        await Esperar(() => _ponte.Recebidas.Count > 0 && depois.ConsultarAsync("C01-P000005-12", 2350, CancellationToken.None).Result?.Aprovado == true);

        // Maquininha que ainda não foi ativada no PagBank
        _ponte.Pronta = false;
        _ponte.MensagemDoOla = "Ative a maquininha no PagBank antes de usar.";
        var nova = Maquininha();
        var erro = await Assert.ThrowsAsync<ErroDeNegocio>(() => nova.CobrarAsync(Cobranca("C01-P000007-20"), null, CancellationToken.None));
        Assert.Equal("Ative a maquininha no PagBank antes de usar.", erro.Message);
        Assert.Equal(1, _ponte.Cobrancas);
    }

    [Fact]
    public async Task Consultar_conta_o_resultado_o_desconhecido_e_quando_nao_deu_para_saber()
    {
        var m = Maquininha();
        _ponte.Guardar("C01-P000001-1", aprovado: true);
        _ponte.Guardar("C01-P000002-2", aprovado: false);
        var aprovado = await m.ConsultarAsync("C01-P000001-1", 1000, CancellationToken.None);
        Assert.True(aprovado!.Aprovado);
        Assert.Contains("NSU 000987", aprovado.Autorizacao);
        Assert.False((await m.ConsultarAsync("C01-P000002-2", 1000, CancellationToken.None))!.Aprovado);
        Assert.Null(await m.ConsultarAsync("C01-P000003-3", 1000, CancellationToken.None));

        _ponte.Desligada = true;
        _ponte.DerrubarLigacao();
        await Assert.ThrowsAsync<MaquininhaSemResposta>(() => m.ConsultarAsync("C01-P000001-1", 1000, CancellationToken.None));
    }

    [Fact]
    public async Task Ao_abrir_o_programa_os_pendentes_sao_resolvidos_e_os_sem_resposta_continuam_esperando()
    {
        using var t = new SistemaTemporario();
        var s = t.Sistema;
        var sessao = s.Caixa.Abrir(1, null, 0);
        Pedido Novo()
        {
            var carrinho = new Carrinho();
            carrinho.Adicionar(s.Catalogo.Produtos().First(p => p.Nome == "PASTEL"), 1);
            return s.Vendas.CriarPedido(sessao, carrinho.Linhas, FormaPagamento.Credito);
        }
        var pago = Novo();
        var nuncaChegou = Novo();
        _ponte.Guardar(VendaServico.IdCobranca(pago), aprovado: true);

        // O identificador da cobrança leva a hora do pedido: não se repete nem quando o número e o id recomeçam
        Assert.Matches(@"^C01-P000001-\d+-\d{12}$", VendaServico.IdCobranca(pago));
        Assert.Equal(pago.Id, s.Vendas.PedidoDaCobranca(VendaServico.IdCobranca(pago))!.Id);
        Assert.Null(s.Vendas.PedidoDaCobranca($"C01-P000001-{pago.Id}-990101000000"));
        Assert.Null(s.Vendas.PedidoDaCobranca("C01-P000005-12"));

        var pagos = await s.Vendas.ResolverPendentesAsync(Maquininha(), CancellationToken.None);
        Assert.Equal([pago.Id], pagos.Select(p => p.Id));
        Assert.Equal(StatusPedido.Pago, s.Vendas.Pedido(pago.Id)!.Status);
        Assert.Contains("NSU 000987", s.Vendas.Pedido(pago.Id)!.Autorizacao);
        Assert.Equal(StatusPedido.Cancelado, s.Vendas.Pedido(nuncaChegou.Id)!.Status);

        // Maquininha desligada: o pedido pode ter sido pago, fica esperando para o operador conferir
        var talvez = Novo();
        _ponte.Desligada = true;
        _ponte.DerrubarLigacao();
        var semResposta = new List<Pedido>();
        Assert.Empty(await s.Vendas.ResolverPendentesAsync(Maquininha(), CancellationToken.None, semResposta));
        Assert.Equal([talvez.Id], semResposta.Select(p => p.Id));
        Assert.Equal(StatusPedido.AguardandoPagamento, s.Vendas.Pedido(talvez.Id)!.Status);

        // A maquininha não sabe dizer (o app dela reiniciou no meio) ou deu um erro qualquer ao perguntar: também
        // fica esperando o operador, nunca é cancelado sozinho
        _ponte.Desligada = false;
        _ponte.GuardarSemSaber(VendaServico.IdCobranca(talvez));
        semResposta.Clear();
        Assert.Empty(await s.Vendas.ResolverPendentesAsync(Maquininha(), CancellationToken.None, semResposta));
        Assert.Equal([talvez.Id], semResposta.Select(p => p.Id));
        semResposta.Clear();
        Assert.Empty(await s.Vendas.ResolverPendentesAsync(new MaquininhaComDefeito(), CancellationToken.None, semResposta));
        Assert.Equal([talvez.Id], semResposta.Select(p => p.Id));
        Assert.Equal(StatusPedido.AguardandoPagamento, s.Vendas.Pedido(talvez.Id)!.Status);
    }

    private sealed class MaquininhaComDefeito : IMaquininha
    {
        public string Nome => "com defeito";
        public Task<ResultadoCobranca> CobrarAsync(Cobranca cobranca, IProgress<string>? andamento, CancellationToken cancelar) =>
            throw new InvalidOperationException("defeito");
        public Task<ResultadoCobranca?> ConsultarAsync(string id, long valorCentavos, CancellationToken cancelar) =>
            throw new InvalidOperationException("defeito");
    }

    [Fact]
    public async Task Maquininha_que_nao_sabe_dizer_se_foi_pago_deixa_para_o_operador()
    {
        var m = Maquininha();
        _ponte.DecidirSozinhaEm = null;
        var cobranca = m.CobrarAsync(Cobranca(), null, CancellationToken.None);
        await Esperar(() => _ponte.Cobrancas == 1);
        _ponte.ConcluirSemSaber();
        var erro = await Assert.ThrowsAsync<MaquininhaSemResposta>(() => cobranca.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("Confira na maquininha", erro.Message);

        // Perguntar de novo (ou mandar cobrar de novo) não decide nada nem cobra outra vez
        await Assert.ThrowsAsync<MaquininhaSemResposta>(() => m.ConsultarAsync("C01-P000005-12", 2350, CancellationToken.None));
        await Assert.ThrowsAsync<MaquininhaSemResposta>(() => m.CobrarAsync(Cobranca(), null, CancellationToken.None));
        Assert.Equal(1, _ponte.Cobrancas);
    }

    [Fact]
    public async Task Resultado_de_outro_valor_nao_vale_nem_como_aprovado()
    {
        var m = Maquininha();
        _ponte.Guardar("C01-P000005-12", aprovado: true, valor: 990);
        // A maquininha confere: o pedido guardado com outro valor não é este
        Assert.False((await m.CobrarAsync(Cobranca(), null, CancellationToken.None)).Aprovado);
        await Assert.ThrowsAsync<MaquininhaSemResposta>(() => m.ConsultarAsync("C01-P000005-12", 2350, CancellationToken.None));
        Assert.Equal(0, _ponte.Cobrancas);

        // Um app que não confere: o tablet confere sozinho
        _ponte.ConfereValor = false;
        var erro = await Assert.ThrowsAsync<MaquininhaSemResposta>(() => m.CobrarAsync(Cobranca(), null, CancellationToken.None));
        Assert.Contains("outro valor (R$ 9,90)", erro.Message);
        await Assert.ThrowsAsync<MaquininhaSemResposta>(() => m.ConsultarAsync("C01-P000005-12", 2350, CancellationToken.None));
        Assert.True((await m.ConsultarAsync("C01-P000005-12", 990, CancellationToken.None))!.Aprovado);
        Assert.Equal(0, _ponte.Cobrancas);
    }

    [Fact]
    public async Task Cancelar_pede_de_novo_ate_a_maquininha_cancelar()
    {
        var m = Maquininha();
        _ponte.DecidirSozinhaEm = null;
        // Os primeiros pedidos chegam antes de o PagBank abrir a tela de pagamento: não há o que cancelar
        _ponte.CancelamentosPerdidos = 2;
        using var cancelar = new CancellationTokenSource();
        var cobranca = m.CobrarAsync(Cobranca(), null, cancelar.Token);
        await Esperar(() => _ponte.Cobrancas == 1);
        cancelar.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cobranca.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, _ponte.Cancelamentos);
    }

    [Fact]
    public async Task Aprovado_que_chega_depois_de_desistir_fica_para_o_aviso()
    {
        var m = Maquininha();
        _ponte.DecidirSozinhaEm = null;
        var cobranca = m.CobrarAsync(Cobranca("C01-P000006-13"), null, CancellationToken.None);
        await Esperar(() => _ponte.Cobrancas == 1);
        // Uma cobrança anterior, de que o operador já tinha desistido, foi aprovada na maquininha
        _ponte.AvisarAprovada("C01-P000005-12");
        _ponte.Concluir(aprovado: true);
        Assert.True((await cobranca.WaitAsync(TimeSpan.FromSeconds(5))).Aprovado);
        Assert.True(m.TirarAprovadaDepois(out var id));
        Assert.Equal("C01-P000005-12", id);
        Assert.False(m.TirarAprovadaDepois(out _));
    }

    [Fact]
    public async Task Maquininha_ativada_depois_cobra_sem_reabrir_o_programa()
    {
        var m = Maquininha();
        _ponte.Pronta = false;
        _ponte.MensagemDoOla = "Ative a maquininha no PagBank antes de usar.";
        await Assert.ThrowsAsync<ErroDeNegocio>(() => m.CobrarAsync(Cobranca(), null, CancellationToken.None));
        // Ativou no PagBank: a próxima venda liga de novo e pergunta outra vez se ela está pronta
        _ponte.Pronta = true;
        Assert.True((await m.CobrarAsync(Cobranca(), null, CancellationToken.None)).Aprovado);
        Assert.Equal(2, _ponte.Ligacoes);
    }

    [Fact]
    public async Task Lixo_na_ligacao_e_ignorado()
    {
        // Um aparelho qualquer que responde linhas sem sentido e uma linha enorme antes do "olá" de verdade
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _ = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            await s.WriteAsync(System.Text.Encoding.UTF8.GetBytes("bom dia\n{quebrado\n" + new string('x', 200_000) + "\n"));
            await s.WriteAsync(ProtocoloPonte.Linha(new MensagemPonte { Tipo = "ola", Versao = 1, Pronta = true, Maquininha = "P2" }));
            await Task.Delay(2000);
        });
        var m = Maquininha($"tcp://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}");
        var info = await m.TestarAsync(CancellationToken.None);
        Assert.Equal("P2", info.Modelo);
        l.Stop();
    }

    [Fact]
    public void Referencia_para_o_PagBank_tem_10_letras_e_numeros_e_nao_se_repete()
    {
        var a = ProtocoloPonte.Referencia("C01-P000005-12-261005201500", 5);
        Assert.Matches("^P0005[A-Z0-9]{5}$", a);
        Assert.Equal(a, ProtocoloPonte.Referencia("C01-P000005-12-261005201500", 5));
        // O mesmo número de pedido em outro dia, outro caixa ou depois de apagar as vendas: outro código
        Assert.NotEqual(a, ProtocoloPonte.Referencia("C01-P000005-12-261006201500", 5));
        Assert.NotEqual(a, ProtocoloPonte.Referencia("C02-P000005-12-261005201500", 5));
        Assert.Matches("^P4567[A-Z0-9]{5}$", ProtocoloPonte.Referencia("x", 1_234_567));
        Assert.All(new[] { ProtocoloPonte.Referencia("", 999_999_999), ProtocoloPonte.Referencia("ç", -3) },
            r => Assert.Matches("^P[0-9]{4}[A-Z0-9]{5}$", r));
        var codigos = Enumerable.Range(1, 5000).Select(n => ProtocoloPonte.Referencia($"C01-P{n:000000}-{n}-261005201500", n));
        Assert.Equal(5000, codigos.Distinct().Count());
        Assert.Equal("1P00000512", MaquininhaPagBank.ReferenciaDe("C01-P000005-12"));
    }

    [Fact]
    public void Ligacao_escrita_na_configuracao()
    {
        Assert.Equal("Bluetooth 00:1A:7D:DA:71:13", ConexaoPonte.Criar("00:1a:7d:da:71:13")!.Descricao);
        Assert.Equal("Bluetooth 00:1A:7D:DA:71:13", ConexaoPonte.Criar("001A7DDA7113")!.Descricao);
        Assert.Equal("Porta COM7", ConexaoPonte.Criar("com7")!.Descricao);
        Assert.Equal("Rede 127.0.0.1:9123", ConexaoPonte.Criar("tcp://127.0.0.1:9123")!.Descricao);
        Assert.Null(ConexaoPonte.Criar(""));
        Assert.Null(ConexaoPonte.Criar("maquininha"));
        Assert.True(BluetoothWindows.TentarLer("00-1A-7D-DA-71-13", out var endereco));
        Assert.Equal(0x001A7DDA7113UL, endereco);
        Assert.Equal("00:1A:7D:DA:71:13", BluetoothWindows.Formatar(endereco));
    }

    [Fact]
    public void Estruturas_do_Bluetooth_do_Windows_tem_o_tamanho_e_as_posicoes_certas()
    {
        // BLUETOOTH_DEVICE_INFO: 560 bytes, endereço em 8, nome (248 letras) em 64 — igual em 32 e 64 bits
        Assert.Equal(560, Marshal.SizeOf<BluetoothWindows.InfoDoAparelho>());
        var nativo = Marshal.AllocHGlobal(560);
        try
        {
            var bytes = new byte[560];
            BitConverter.GetBytes(0x001A7DDA7113UL).CopyTo(bytes, 8);
            BitConverter.GetBytes(1).CopyTo(bytes, 20);
            BitConverter.GetBytes(1).CopyTo(bytes, 28);
            System.Text.Encoding.Unicode.GetBytes("Moderninha Smart 2").CopyTo(bytes, 64);
            Marshal.Copy(bytes, 0, nativo, 560);
            var info = Marshal.PtrToStructure<BluetoothWindows.InfoDoAparelho>(nativo);
            Assert.Equal((0x001A7DDA7113UL, 1, 1, "Moderninha Smart 2"), (info.Endereco, info.Conectado, info.Autenticado, info.Nome));
        }
        finally
        {
            Marshal.FreeHGlobal(nativo);
        }
        // BLUETOOTH_DEVICE_SEARCH_PARAMS: 32 bytes em 32 bits, 40 em 64 bits
        Assert.Equal(IntPtr.Size == 8 ? 40 : 32, Marshal.SizeOf<BluetoothWindows.ParametrosDaBusca>());
        // SOCKADDR_BTH: família, endereço (2), serviço (10) e canal (26) em 30 bytes (o .NET só monta no Windows)
        if (OperatingSystem.IsWindows())
        {
            var s = new EnderecoBluetooth(0x001A7DDA7113UL, ProtocoloPonte.ServicoBluetooth).Serialize();
            Assert.Equal(30, s.Size);
            Assert.Equal(0x13, s[2]);
            Assert.Equal(ProtocoloPonte.ServicoBluetooth.ToByteArray()[0], s[10]);
            Assert.Equal(0, s[26]);
            // A lista dos pareados funciona mesmo sem Bluetooth no computador (vem vazia)
            Assert.NotNull(BluetoothWindows.Pareados());
        }
        else Assert.Empty(BluetoothWindows.Pareados());
    }

    private static async Task Esperar(Func<bool> condicao, int milissegundos = 5000)
    {
        var limite = DateTime.UtcNow.AddMilliseconds(milissegundos);
        while (!condicao())
        {
            if (DateTime.UtcNow > limite) throw new TimeoutException("A condição não aconteceu a tempo.");
            await Task.Delay(20);
        }
    }
}
