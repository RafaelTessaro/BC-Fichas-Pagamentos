using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BCFichas.Core.Pagamento;

namespace BCFichas.Tests.Core;

/// <summary>
/// Uma Moderninha Smart 2 de mentira: faz o papel do app ponte (cartao/ponte-android) pela rede, com as mesmas
/// mensagens e as mesmas regras (guarda o resultado de cada cobrança, nunca cobra o mesmo pedido duas vezes, uma
/// cobrança por vez). Os testes decidem o que o "cliente" faz e podem derrubar a ligação no meio.
/// </summary>
public sealed class PonteFalsa : IDisposable
{
    private readonly TcpListener _servidor = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _parar = new();
    private readonly object _trava = new();
    private readonly ConcurrentDictionary<string, MensagemPonte> _resultados = new();
    private TcpClient? _atual;
    private string? _emAndamento;
    private TaskCompletionSource<MensagemPonte>? _decisao;

    public PonteFalsa()
    {
        _servidor.Start();
        _ = Task.Run(Atender);
    }

    public int Porta => ((IPEndPoint)_servidor.LocalEndpoint).Port;
    public string Ligacao => $"tcp://127.0.0.1:{Porta}";

    // ---------- O que a maquininha faz ----------
    public bool Pronta { get; set; } = true;
    public string MensagemDoOla { get; set; } = "";

    /// <summary>Sozinha: aprova (ou recusa) depois desse tempo. Nulo: espera o teste chamar <see cref="Concluir"/>.</summary>
    public TimeSpan? DecidirSozinhaEm { get; set; } = TimeSpan.FromMilliseconds(150);

    public bool Aprovar { get; set; } = true;

    /// <summary>Aceita o cancelamento pedido pelo tablet (falso: o cliente já está pagando).</summary>
    public bool AceitaCancelar { get; set; } = true;

    /// <summary>Derruba a ligação logo depois de receber a cobrança (a cobrança continua na maquininha).</summary>
    public bool CairAoReceberCobranca { get; set; }

    /// <summary>Para de responder (nem "pong"), mas deixa a ligação aberta, como um Bluetooth que morreu.</summary>
    public bool Muda { get; set; }

    /// <summary>Recusa ligações novas (maquininha desligada ou longe).</summary>
    public bool Desligada { get; set; }

    // ---------- O que aconteceu ----------
    public int Cobrancas;
    public int Ligacoes;
    public int Cancelamentos;
    public ConcurrentQueue<MensagemPonte> Recebidas { get; } = new();

    public void Concluir(bool aprovado) =>
        _decisao?.TrySetResult(aprovado ? Aprovada() : new MensagemPonte { Aprovado = false, Mensagem = "Cartão recusado" });

    /// <summary>Uma cobrança que a maquininha já tinha feito (ex.: antes de o programa do tablet fechar).</summary>
    public void Guardar(string id, bool aprovado)
    {
        var m = aprovado ? Aprovada() : new MensagemPonte { Aprovado = false, Mensagem = "Não aprovado" };
        m.Tipo = "resultado";
        m.Id = id;
        _resultados[id] = m;
    }

    public void DerrubarLigacao()
    {
        lock (_trava)
        {
            _atual?.Dispose();
            _atual = null;
        }
    }

    private static MensagemPonte Aprovada() => new()
    {
        Aprovado = true, Mensagem = "Pagamento aprovado", Autorizacao = "123456", Nsu = "000987", Bandeira = "visa",
        Codigo = "ABC123XYZ",
    };

    private async Task Atender()
    {
        while (!_parar.IsCancellationRequested)
        {
            TcpClient cliente;
            try
            {
                cliente = await _servidor.AcceptTcpClientAsync(_parar.Token);
            }
            catch (Exception)
            {
                return;
            }
            if (Desligada)
            {
                cliente.Dispose();
                continue;
            }
            Interlocked.Increment(ref Ligacoes);
            lock (_trava)
            {
                // Como o Bluetooth: uma ligação por vez, a nova toma o lugar da velha
                _atual?.Dispose();
                _atual = cliente;
            }
            _ = Task.Run(() => Conversar(cliente));
        }
    }

    private async Task Conversar(TcpClient cliente)
    {
        try
        {
            using var leitor = new StreamReader(cliente.GetStream(), Encoding.UTF8);
            while (await leitor.ReadLineAsync(_parar.Token) is { } linha)
            {
                var m = ProtocoloPonte.Ler(linha);
                if (m is null) continue;
                // Como o app ponte: o que chega por uma ligação que já foi trocada por outra é ignorado (o tablet
                // já está perguntando pela ligação nova)
                lock (_trava)
                {
                    if (_atual != cliente) return;
                    Recebidas.Enqueue(m);
                    if (Muda) continue;
                    Tratar(cliente, m);
                }
            }
        }
        catch (Exception)
        {
            // Ligação caiu
        }
    }

    private void Tratar(TcpClient cliente, MensagemPonte m)
    {
        switch (m.Tipo)
        {
            case "ola":
                Enviar(cliente, new MensagemPonte
                {
                    Tipo = "ola", Versao = ProtocoloPonte.Versao, Maquininha = "PagBank Moderninha Smart 2", Serial = "PB0123456",
                    Pronta = Pronta, Mensagem = MensagemDoOla,
                });
                break;
            case "ping":
                Enviar(cliente, new MensagemPonte { Tipo = "pong" });
                break;
            case "consultar":
                Responder(cliente, m.Id!);
                break;
            case "cancelar":
                Interlocked.Increment(ref Cancelamentos);
                if (AceitaCancelar && m.Id == _emAndamento)
                    _decisao?.TrySetResult(new MensagemPonte { Aprovado = false, Cancelado = true, Mensagem = "Operação cancelada" });
                break;
            case "cobrar":
                Cobrar(cliente, m);
                break;
        }
    }

    private void Responder(TcpClient cliente, string id)
    {
        if (_resultados.TryGetValue(id, out var r)) Enviar(cliente, r);
        else if (_emAndamento == id)
            Enviar(cliente, new MensagemPonte { Tipo = "andamento", Id = id, Texto = "Pagamento em andamento na maquininha" });
        else Enviar(cliente, new MensagemPonte { Tipo = "desconhecida", Id = id });
    }

    private void Cobrar(TcpClient cliente, MensagemPonte m)
    {
        var id = m.Id!;
        lock (_trava)
        {
            if (_resultados.ContainsKey(id) || _emAndamento == id)
            {
                Responder(cliente, id);
                return;
            }
            if (_emAndamento is not null)
            {
                Enviar(cliente, new MensagemPonte { Tipo = "ocupada", Id = id, Mensagem = "A maquininha está fazendo outro pagamento." });
                return;
            }
            _emAndamento = id;
            _decisao = new TaskCompletionSource<MensagemPonte>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Interlocked.Increment(ref Cobrancas);
        var decisao = _decisao;
        Enviar(cliente, new MensagemPonte { Tipo = "andamento", Id = id, Texto = m.Forma == "pix" ? "QR Code na tela" : "Aproxime, insira ou passe o cartão" });
        if (CairAoReceberCobranca) DerrubarLigacao();
        if (DecidirSozinhaEm is { } tempo)
            _ = Task.Delay(tempo).ContinueWith(_ => decisao.TrySetResult(Aprovar ? Aprovada()
                : new MensagemPonte { Aprovado = false, Mensagem = "Cartão recusado" }), TaskScheduler.Default);
        _ = decisao.Task.ContinueWith(t =>
        {
            var r = t.Result;
            r.Tipo = "resultado";
            r.Id = id;
            _resultados[id] = r;
            lock (_trava) _emAndamento = null;
            // A resposta vai para a ligação que estiver aberta agora (o tablet pode ter ligado de novo)
            if (_atual is { } atual && !Muda) Enviar(atual, r);
        }, TaskScheduler.Default);
    }

    private void Enviar(TcpClient cliente, MensagemPonte m)
    {
        try
        {
            var bytes = ProtocoloPonte.Linha(m);
            lock (cliente) cliente.GetStream().Write(bytes);
        }
        catch (Exception)
        {
            // Ligação caiu: o tablet consulta depois
        }
    }

    public void Dispose()
    {
        _parar.Cancel();
        _servidor.Stop();
        DerrubarLigacao();
        _decisao?.TrySetCanceled();
    }
}
