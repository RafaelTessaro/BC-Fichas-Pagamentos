using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace BCFichas.Core.Pagamento;

/// <summary>
/// A cobrança já tinha ido para a maquininha e ela parou de responder: não dá para saber se o cliente pagou. O
/// pedido continua aguardando pagamento (não pode ser cancelado sozinho) até alguém conferir na maquininha.
/// </summary>
public sealed class MaquininhaSemResposta(string mensagem) : Exception(mensagem);

/// <summary>O que o app ponte contou da maquininha ao ligar.</summary>
public sealed record InfoMaquininha(string Modelo, string Serial, bool Pronta, string Mensagem);

/// <summary>Quanto o tablet espera a maquininha em cada situação.</summary>
public sealed record TemposMaquininha(TimeSpan EsperaLigar, TimeSpan IntervaloPing, TimeSpan Silencio,
    TimeSpan EsperaReligar, TimeSpan EsperaConsulta)
{
    public static readonly TemposMaquininha Reais = new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(3));

    /// <summary>Os de toda maquininha nova (os testes das telas trocam por tempos curtos).</summary>
    public static TemposMaquininha Padrao { get; set; } = Reais;
}

/// <summary>
/// Moderninha Smart 2 do PagBank ligada ao tablet por Bluetooth. Dentro dela roda o app ponte do BC Fichas
/// (cartao/ponte-android), que recebe o valor, chama o pagamento do PagBank (PlugPagServiceWrapper) e devolve o
/// resultado. O cliente paga na maquininha; o tablet só manda o valor e espera.
/// </summary>
/// <remarks>
/// Nunca cobra duas vezes: cada pedido tem um identificador fixo e o app ponte guarda o resultado de cada um. Se a
/// ligação cair no meio, o tablet liga de novo e pergunta pelo mesmo identificador. Cancelar no tablet só pede para a
/// maquininha cancelar: se o cliente já tinha pago, vale o pagamento. Quando a maquininha não sabe dizer se a
/// cobrança foi paga ("indefinida"), o tablet não decide: o operador confere na maquininha.
/// </remarks>
public sealed class MaquininhaPagBank : IMaquininha, IDisposable
{
    private readonly IConexaoPonte? _conexao;
    private readonly SemaphoreSlim _uso = new(1, 1);
    private Ligacao? _ligacao;
    private volatile bool _descartada;
    private readonly ConcurrentQueue<string> _aprovadasDepois = new();
    private readonly ConcurrentDictionary<string, long> _conferirDepois = new();

    /// <param name="conexao">Nulo: a maquininha ainda não foi escolhida nas configurações.</param>
    public MaquininhaPagBank(IConexaoPonte? conexao, int caixa, bool comprovante, string programa = "BC Fichas Cartão")
    {
        _conexao = conexao;
        Caixa = caixa;
        Comprovante = comprovante;
        Programa = programa;
    }

    public string Nome => "Moderninha Smart 2";
    public IConexaoPonte? Conexao => _conexao;
    public int Caixa { get; }

    /// <summary>Imprimir a via do estabelecimento na maquininha.</summary>
    public bool Comprovante { get; }

    public string Programa { get; }

    /// <summary>O que o app ponte contou na última vez que ligou.</summary>
    public InfoMaquininha? Info { get; private set; }

    /// <summary>
    /// Uma cobrança que a maquininha contou como aprovada quando o tablet já não esperava por ela (o operador tinha
    /// desistido de esperar e tocado em "Não foi pago"). Quem mostra o aviso tira daqui e confere o pedido.
    /// </summary>
    public bool TirarAprovadaDepois(out string id) => _aprovadasDepois.TryDequeue(out id!);

    /// <summary>
    /// O operador decidiu, sem a maquininha responder, que uma cobrança não foi paga. Nas próximas vezes que ligar, o
    /// tablet pergunta por ela à maquininha: se aparecer aprovada, vira um aviso (<see cref="TirarAprovadaDepois"/>).
    /// </summary>
    public void ConferirDepois(string id, long valorCentavos)
    {
        if (_conferirDepois.Count < 20) _conferirDepois[id] = valorCentavos;
    }

    // Tempos (os testes usam tempos curtos)
    /// <summary>Quanto espera a ligação Bluetooth abrir e o app ponte responder.</summary>
    public TimeSpan EsperaLigar { get; init; } = TemposMaquininha.Padrao.EsperaLigar;

    /// <summary>De quanto em quanto tempo pergunta "está aí?" enquanto espera o cliente pagar.</summary>
    public TimeSpan IntervaloPing { get; init; } = TemposMaquininha.Padrao.IntervaloPing;

    /// <summary>Sem ouvir nada da maquininha por esse tempo: a ligação caiu.</summary>
    public TimeSpan Silencio { get; init; } = TemposMaquininha.Padrao.Silencio;

    /// <summary>Caiu no meio do pagamento: tenta ligar de novo por esse tempo antes de desistir.</summary>
    public TimeSpan EsperaReligar { get; init; } = TemposMaquininha.Padrao.EsperaReligar;

    /// <summary>Consulta de um pagamento ainda em andamento na maquininha: espera até esse tempo.</summary>
    public TimeSpan EsperaConsulta { get; init; } = TemposMaquininha.Padrao.EsperaConsulta;

    /// <summary>Liga na maquininha e conta o que o app ponte respondeu (botão "Testar" das configurações).</summary>
    public async Task<InfoMaquininha> TestarAsync(CancellationToken cancelar)
    {
        await _uso.WaitAsync(cancelar);
        try
        {
            _ligacao?.Dispose();
            _ligacao = null;
            await Ligar(cancelar);
            return Info!;
        }
        catch (Exception e) when (!cancelar.IsCancellationRequested)
        {
            throw new ErroDeNegocio(Explicar(e));
        }
        finally
        {
            _uso.Release();
        }
    }

    public async Task<ResultadoCobranca> CobrarAsync(Cobranca cobranca, IProgress<string>? andamento,
        CancellationToken cancelar)
    {
        var forma = ProtocoloPonte.Forma(cobranca.Forma);
        await _uso.WaitAsync(cancelar);
        try
        {
            andamento?.Report("Ligando na maquininha...");
            Ligacao ligacao;
            try
            {
                ligacao = await Ligar(cancelar);
            }
            catch (Exception e) when (!cancelar.IsCancellationRequested)
            {
                // Nada foi para a maquininha: o pedido pode ser cancelado sem medo
                throw new ErroDeNegocio(Explicar(e));
            }
            if (Info is { Pronta: false } info)
                throw new ErroDeNegocio(info.Mensagem.Length > 0 ? info.Mensagem : "A maquininha não está pronta para cobrar.");
            PerguntarPelasDesistidas(ligacao, cobranca.Id);

            var pedido = new MensagemPonte
            {
                Tipo = "cobrar",
                Id = cobranca.Id,
                Referencia = cobranca.Referencia ?? ReferenciaDe(cobranca.Id),
                Valor = cobranca.ValorCentavos,
                Forma = forma,
                Comprovante = Comprovante,
            };
            try
            {
                ligacao.Enviar(pedido);
            }
            catch (LigacaoPerdida)
            {
                // Pode ter chegado ou não: liga de novo e pergunta (o app ponte nunca cobra o mesmo pedido duas vezes)
                ligacao = await Religar(cobranca.Id, cobranca.ValorCentavos, andamento);
            }
            andamento?.Report(cobranca.Forma == FormaPagamento.Pix
                ? "Mostrando o QR Code do PIX na maquininha..."
                : "Insira, passe ou aproxime o cartão na maquininha");

            var pediuCancelar = false;
            var proximoCancelar = 0L;
            while (true)
            {
                // Pede de novo de tempos em tempos até a resposta chegar: o pedido pode chegar antes de o PagBank
                // abrir a tela de pagamento (e aí não teria o que cancelar)
                if (cancelar.IsCancellationRequested && Environment.TickCount64 >= proximoCancelar)
                {
                    if (!pediuCancelar) andamento?.Report("Pedindo para a maquininha cancelar...");
                    pediuCancelar = true;
                    proximoCancelar = Environment.TickCount64 + (long)IntervaloPing.TotalMilliseconds;
                    try
                    {
                        ligacao.Enviar(new MensagemPonte { Tipo = "cancelar", Id = cobranca.Id });
                    }
                    catch (LigacaoPerdida)
                    {
                        // A ligação caiu: a espera abaixo percebe, liga de novo e pede de novo
                    }
                }

                MensagemPonte? m;
                try
                {
                    m = await ligacao.Proxima(TimeSpan.FromMilliseconds(400), IntervaloPing, Silencio, CancellationToken.None);
                }
                catch (LigacaoPerdida)
                {
                    ligacao = await Religar(cobranca.Id, cobranca.ValorCentavos, andamento);
                    // Pediu para cancelar na ligação que caiu: pede de novo na nova
                    proximoCancelar = 0;
                    continue;
                }
                if (m is null) continue;
                if (m.Id is not null && m.Id != cobranca.Id)
                {
                    OutraCobranca(m);
                    continue;
                }

                switch (m.Tipo)
                {
                    case "andamento":
                        if (!string.IsNullOrWhiteSpace(m.Texto))
                            andamento?.Report(cancelar.IsCancellationRequested ? "Cancelando... " + m.Texto : m.Texto);
                        break;
                    case "resultado":
                        ConferirValor(m, cobranca.ValorCentavos);
                        // Cancelado como o operador pediu. Se o cliente pagou antes de o cancelamento chegar, vale o pagamento.
                        if (m.Aprovado != true && m.Cancelado == true && cancelar.IsCancellationRequested)
                            throw new OperationCanceledException(cancelar);
                        return Resultado(m);
                    case "indefinida":
                        throw new MaquininhaSemResposta(Texto(m.Mensagem,
                            "A maquininha não conseguiu saber se o pagamento foi aprovado. Confira nela."));
                    case "ocupada":
                        return new ResultadoCobranca(false, Texto(m.Mensagem,
                            "A maquininha está ocupada com outro pagamento. Termine o outro pagamento nela e tente de novo."));
                    case "desconhecida":
                        return new ResultadoCobranca(false, "A cobrança não chegou na maquininha. Tente de novo.");
                    case "erro":
                        return new ResultadoCobranca(false, Texto(m.Mensagem, "A maquininha não conseguiu cobrar."));
                }
            }
        }
        finally
        {
            _uso.Release();
        }
    }

    /// <summary>
    /// Pergunta o que aconteceu com uma cobrança: o resultado, nulo se ela nunca chegou na maquininha, ou
    /// <see cref="MaquininhaSemResposta"/> se não deu para saber (maquininha fora do alcance, pagamento ainda em
    /// andamento, a maquininha não sabe dizer).
    /// </summary>
    public async Task<ResultadoCobranca?> ConsultarAsync(string id, long valorCentavos, CancellationToken cancelar)
    {
        await _uso.WaitAsync(cancelar);
        try
        {
            Ligacao ligacao;
            try
            {
                ligacao = await Ligar(cancelar);
                PerguntarPelasDesistidas(ligacao, id);
                ligacao.Enviar(new MensagemPonte { Tipo = "consultar", Id = id, Valor = valorCentavos });
            }
            catch (Exception e) when (!cancelar.IsCancellationRequested)
            {
                throw new MaquininhaSemResposta(Explicar(e));
            }

            var limite = Environment.TickCount64 + (long)EsperaConsulta.TotalMilliseconds;
            while (true)
            {
                cancelar.ThrowIfCancellationRequested();
                if (Environment.TickCount64 > limite)
                    throw new MaquininhaSemResposta("O pagamento ainda está em andamento na maquininha. Termine nela e consulte de novo.");
                MensagemPonte? m;
                try
                {
                    m = await ligacao.Proxima(TimeSpan.FromMilliseconds(400), IntervaloPing, Silencio, cancelar);
                }
                catch (LigacaoPerdida)
                {
                    throw new MaquininhaSemResposta("A ligação com a maquininha caiu.");
                }
                if (m is null) continue;
                if (m.Id is not null && m.Id != id)
                {
                    OutraCobranca(m);
                    continue;
                }
                switch (m.Tipo)
                {
                    case "resultado":
                        ConferirValor(m, valorCentavos);
                        return Resultado(m);
                    case "desconhecida":
                        return null;
                    case "indefinida":
                        throw new MaquininhaSemResposta(Texto(m.Mensagem,
                            "A maquininha não conseguiu saber se o pagamento foi aprovado. Confira nela."));
                    case "erro":
                        throw new MaquininhaSemResposta(Texto(m.Mensagem, "A maquininha não conseguiu consultar o pagamento."));
                }
            }
        }
        finally
        {
            _uso.Release();
        }
    }

    /// <summary>Fecha a ligação (programa fechando ou maquininha trocada nas configurações).</summary>
    public void Dispose()
    {
        _descartada = true;
        _ligacao?.Dispose();
        _ligacao = null;
    }

    /// <summary>
    /// A ligação aberta (se ela ainda responde) ou uma nova: abre, diz "olá" e guarda o que a maquininha contou.
    /// </summary>
    private async Task<Ligacao> Ligar(CancellationToken cancelar)
    {
        ObjectDisposedException.ThrowIf(_descartada, this);
        if (_conexao is null)
            throw new ErroDeNegocio("Escolha a maquininha em Configurações → Maquininha.");
        if (_ligacao is { } atual)
        {
            // Não estava pronta (não ativada no PagBank): liga de novo para saber se já está
            if (Info?.Pronta != false && await atual.Responde(TimeSpan.FromSeconds(3), cancelar)) return atual;
            atual.Dispose();
            _ligacao = null;
        }

        var fluxo = await _conexao.AbrirAsync(EsperaLigar, cancelar);
        var nova = new Ligacao(fluxo);
        try
        {
            nova.Enviar(new MensagemPonte { Tipo = "ola", Versao = ProtocoloPonte.Versao, Caixa = Caixa, Programa = Programa });
            var limite = Environment.TickCount64 + (long)EsperaLigar.TotalMilliseconds;
            while (true)
            {
                var resta = limite - Environment.TickCount64;
                if (resta <= 0) throw new TimeoutException("O app BC Fichas da maquininha não respondeu.");
                var m = await nova.Proxima(TimeSpan.FromMilliseconds(resta), IntervaloPing, EsperaLigar, cancelar);
                if (m?.Tipo != "ola") continue;
                if (m.Versao is not { } versao || versao < 1)
                    throw new ErroDeNegocio("O app da maquininha não é o do BC Fichas.");
                if (versao > ProtocoloPonte.Versao)
                    throw new ErroDeNegocio("O app da maquininha é mais novo que este programa: atualize o BC Fichas Cartão.");
                Info = new InfoMaquininha(Texto(m.Maquininha, "Moderninha Smart 2"), m.Serial ?? "", m.Pronta != false,
                    m.Mensagem ?? "");
                _ligacao = nova;
                return nova;
            }
        }
        catch (Exception)
        {
            nova.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A ligação caiu depois de a cobrança ter sido enviada: liga de novo e pergunta pela mesma cobrança. Se não
    /// conseguir em <see cref="EsperaReligar"/>, não dá para saber se o cliente pagou.
    /// </summary>
    private async Task<Ligacao> Religar(string id, long valorCentavos, IProgress<string>? andamento)
    {
        _ligacao?.Dispose();
        _ligacao = null;
        var limite = Environment.TickCount64 + (long)EsperaReligar.TotalMilliseconds;
        while (true)
        {
            andamento?.Report("A ligação com a maquininha caiu. Ligando de novo...");
            try
            {
                var ligacao = await Ligar(CancellationToken.None);
                ligacao.Enviar(new MensagemPonte { Tipo = "consultar", Id = id, Valor = valorCentavos });
                andamento?.Report("Ligado de novo. Esperando a maquininha...");
                return ligacao;
            }
            catch (Exception) when (Environment.TickCount64 < limite && !_descartada)
            {
                _ligacao?.Dispose();
                _ligacao = null;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, IntervaloPing.TotalMilliseconds)));
            }
            catch (Exception)
            {
                _ligacao?.Dispose();
                _ligacao = null;
                throw new MaquininhaSemResposta(
                    "A maquininha parou de responder no meio do pagamento. Confira nela se o pagamento foi aprovado.");
            }
        }
    }

    private static ResultadoCobranca Resultado(MensagemPonte m)
    {
        var aprovado = m.Aprovado == true;
        var mensagem = Texto(m.Mensagem, aprovado ? "Pagamento aprovado"
            : m.Cancelado == true ? "Pagamento cancelado na maquininha" : "Pagamento não aprovado");
        if (!aprovado) return new ResultadoCobranca(false, mensagem);
        // Tudo o que identifica o pagamento no PagBank (para conferir ou estornar pelo PagBank)
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Bandeira)) partes.Add(m.Bandeira.Trim().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(m.Autorizacao)) partes.Add("aut " + m.Autorizacao.Trim());
        if (!string.IsNullOrWhiteSpace(m.Nsu)) partes.Add("NSU " + m.Nsu.Trim());
        if (!string.IsNullOrWhiteSpace(m.Codigo)) partes.Add("cód " + m.Codigo.Trim());
        var autorizacao = string.Join(" • ", partes);
        return new ResultadoCobranca(true, mensagem, autorizacao.Length == 0 ? null : autorizacao[..Math.Min(200, autorizacao.Length)]);
    }

    /// <summary>
    /// O resultado é de outra cobrança com o mesmo identificador (não devia acontecer): não vale nem como aprovado
    /// nem como recusado. O operador confere na maquininha.
    /// </summary>
    private static void ConferirValor(MensagemPonte m, long valorCentavos)
    {
        if (m.Valor is { } valor && valor != valorCentavos)
            throw new MaquininhaSemResposta(
                $"A maquininha respondeu com outro valor ({Dinheiro.Formatar(valor)}). Confira nela se o pagamento de {Dinheiro.Formatar(valorCentavos)} foi aprovado.");
    }

    private void PerguntarPelasDesistidas(Ligacao ligacao, string atual)
    {
        foreach (var (id, valor) in _conferirDepois)
        {
            if (id == atual) continue;
            try
            {
                ligacao.Enviar(new MensagemPonte { Tipo = "consultar", Id = id, Valor = valor });
            }
            catch (LigacaoPerdida)
            {
                return;
            }
        }
    }

    /// <summary>Resposta sobre outra cobrança (uma das desistidas, ou um resultado que chegou atrasado).</summary>
    private void OutraCobranca(MensagemPonte m)
    {
        if (m.Id is not { Length: > 0 } id) return;
        switch (m.Tipo)
        {
            case "resultado":
                var conferindo = _conferirDepois.TryRemove(id, out var valor);
                if (m.Aprovado == true && !(conferindo && m.Valor is { } v && v != valor)) _aprovadasDepois.Enqueue(id);
                break;
            case "desconhecida" or "indefinida" or "erro":
                // A maquininha não tem nada a acrescentar: fica como o operador decidiu
                _conferirDepois.TryRemove(id, out _);
                break;
        }
    }

    /// <summary>Sem a referência pronta: as últimas 10 letras e números do identificador.</summary>
    internal static string ReferenciaDe(string id)
    {
        var limpo = new string(id.Where(char.IsAsciiLetterOrDigit).ToArray());
        return limpo.Length <= 10 ? limpo : limpo[^10..];
    }

    private static string Texto(string? texto, string padrao) => string.IsNullOrWhiteSpace(texto) ? padrao : texto.Trim();

    private string Explicar(Exception e) => e switch
    {
        ErroDeNegocio n => n.Message,
        MaquininhaSemResposta s => s.Message,
        _ when _conexao is ConexaoSerial s =>
            $"Não consegui ligar na maquininha pela {s.Descricao.ToLowerInvariant()}. Confira se ela está ligada, perto do tablet e com o app BC Fichas aberto.",
        _ => "Não consegui ligar na maquininha por Bluetooth. Confira se ela está ligada, perto do tablet e com o app BC Fichas aberto.",
    };

    /// <summary>A ligação ficou muda ou fechou.</summary>
    private sealed class LigacaoPerdida : Exception;

    /// <summary>
    /// Uma ligação aberta com o app ponte. Uma linha (thread) só lê o que chega, linha por linha, e guarda as
    /// mensagens; quem espera a resposta pega dali. Outra só escreve, na ordem: quem envia não espera o Bluetooth
    /// (a tela nunca trava numa ligação lenta).
    /// </summary>
    private sealed class Ligacao : IDisposable
    {
        private readonly Stream _fluxo;
        private readonly Channel<MensagemPonte> _entrada =
            Channel.CreateUnbounded<MensagemPonte>(new UnboundedChannelOptions { SingleReader = true });
        private readonly BlockingCollection<byte[]> _saida = new();
        private long _ultimaMensagem = Environment.TickCount64;
        private long _ultimoPing = Environment.TickCount64;
        /// <summary>Quantas vezes chegou alguma coisa (o relógio do sistema anda de poucos em poucos milissegundos).</summary>
        private long _chegadas;
        private volatile bool _caiu;

        public Ligacao(Stream fluxo)
        {
            _fluxo = fluxo;
            new Thread(Ler) { IsBackground = true, Name = "Maquininha (leitura)" }.Start();
            new Thread(Escrever) { IsBackground = true, Name = "Maquininha (envio)" }.Start();
        }

        private long Silencio => Environment.TickCount64 - Interlocked.Read(ref _ultimaMensagem);

        /// <summary>Põe na fila de envio. Se a ligação cair antes de enviar, quem espera a resposta percebe.</summary>
        public void Enviar(MensagemPonte mensagem)
        {
            if (_caiu) throw new LigacaoPerdida();
            try
            {
                _saida.Add(ProtocoloPonte.Linha(mensagem));
            }
            catch (InvalidOperationException)
            {
                // A fila já foi fechada (a ligação caiu)
                throw new LigacaoPerdida();
            }
        }

        private void Escrever()
        {
            try
            {
                foreach (var bytes in _saida.GetConsumingEnumerable())
                {
                    _fluxo.Write(bytes, 0, bytes.Length);
                    _fluxo.Flush();
                }
            }
            catch (Exception)
            {
                // Ligação fechada ou caiu: fecha tudo (a leitura para e quem espera a resposta liga de novo)
                Dispose();
            }
        }

        /// <summary>A próxima mensagem, ou nulo depois de <paramref name="ate"/>. Pergunta "está aí?" de tempos em tempos.</summary>
        public async Task<MensagemPonte?> Proxima(TimeSpan ate, TimeSpan intervaloPing, TimeSpan silencio, CancellationToken cancelar)
        {
            var limite = Environment.TickCount64 + (long)ate.TotalMilliseconds;
            while (true)
            {
                if (_entrada.Reader.TryRead(out var m)) return m;
                if (_caiu || Silencio > silencio.TotalMilliseconds)
                {
                    // O que chegou logo antes de a ligação fechar ainda vale (a leitura guarda antes de marcar que caiu)
                    if (_entrada.Reader.TryRead(out m)) return m;
                    throw new LigacaoPerdida();
                }
                var agora = Environment.TickCount64;
                if (agora - Interlocked.Read(ref _ultimoPing) >= intervaloPing.TotalMilliseconds)
                {
                    Interlocked.Exchange(ref _ultimoPing, agora);
                    Enviar(new MensagemPonte { Tipo = "ping" });
                }
                var resta = limite - agora;
                if (resta <= 0) return null;
                var fatia = Math.Max(10, Math.Min(resta, Math.Min(250, (long)intervaloPing.TotalMilliseconds)));
                using var espera = CancellationTokenSource.CreateLinkedTokenSource(cancelar);
                espera.CancelAfter(TimeSpan.FromMilliseconds(fatia));
                try
                {
                    if (!await _entrada.Reader.WaitToReadAsync(espera.Token)) throw new LigacaoPerdida();
                }
                catch (OperationCanceledException) when (!cancelar.IsCancellationRequested)
                {
                    // Só passou a fatia de tempo
                }
            }
        }

        /// <summary>A ligação parada ainda responde? (o Bluetooth cai sem avisar quando a maquininha desliga)</summary>
        public async Task<bool> Responde(TimeSpan espera, CancellationToken cancelar)
        {
            if (_caiu) return false;
            var antes = Interlocked.Read(ref _chegadas);
            try
            {
                Enviar(new MensagemPonte { Tipo = "ping" });
                var limite = Environment.TickCount64 + (long)espera.TotalMilliseconds;
                while (Environment.TickCount64 < limite)
                {
                    if (_caiu) return false;
                    if (Interlocked.Read(ref _chegadas) != antes) return true;
                    await Task.Delay(20, cancelar);
                }
            }
            catch (LigacaoPerdida)
            {
            }
            return false;
        }

        private void Ler()
        {
            var buffer = new byte[4096];
            var linha = new MemoryStream();
            var descartando = false;
            try
            {
                while (true)
                {
                    var lidos = _fluxo.Read(buffer, 0, buffer.Length);
                    if (lidos <= 0) break;
                    Interlocked.Exchange(ref _ultimaMensagem, Environment.TickCount64);
                    Interlocked.Increment(ref _chegadas);
                    for (var i = 0; i < lidos; i++)
                    {
                        if (buffer[i] != (byte)'\n')
                        {
                            if (linha.Length < ProtocoloPonte.MaiorLinha) linha.WriteByte(buffer[i]);
                            else descartando = true;
                            continue;
                        }
                        if (!descartando)
                        {
                            var m = ProtocoloPonte.Ler(Encoding.UTF8.GetString(linha.GetBuffer(), 0, (int)linha.Length).TrimEnd('\r'));
                            // "pong" só serve para saber que a ligação está viva (já marcado acima)
                            if (m is not null && m.Tipo != "pong") _entrada.Writer.TryWrite(m);
                        }
                        linha.SetLength(0);
                        descartando = false;
                    }
                }
            }
            catch (Exception)
            {
                // Ligação fechada ou caiu
            }
            finally
            {
                _caiu = true;
                _entrada.Writer.TryComplete();
                _saida.CompleteAdding();
            }
        }

        public void Dispose()
        {
            _caiu = true;
            try
            {
                _saida.CompleteAdding();
            }
            catch (ObjectDisposedException)
            {
            }
            try
            {
                _fluxo.Dispose();
            }
            catch (Exception)
            {
                // Já estava fechada
            }
        }
    }
}
