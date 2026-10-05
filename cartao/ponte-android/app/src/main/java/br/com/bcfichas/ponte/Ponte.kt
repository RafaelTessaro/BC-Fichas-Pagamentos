package br.com.bcfichas.ponte

import java.util.concurrent.Executor

/** O que a maquininha é e se está pronta para cobrar (ativada no PagBank). */
data class InfoTerminal(val modelo: String, val serial: String, val pronta: Boolean, val mensagem: String)

/** Uma cobrança pedida pelo tablet. */
data class PedidoPagamento(
    val id: String,
    val referencia: String,
    val valor: Long,
    val forma: String,
    val comprovante: Boolean,
)

data class ResultadoPagamento(
    val aprovado: Boolean,
    val cancelado: Boolean = false,
    val mensagem: String,
    val autorizacao: String? = null,
    val nsu: String? = null,
    val bandeira: String? = null,
    val codigo: String? = null,
    /** O código da venda que o PagBank registrou (para conferir a última aprovada). */
    val referencia: String? = null,
    /** Em centavos: o da cobrança (guardado junto do resultado) ou o que o PagBank registrou (na última aprovada). */
    val valor: Long? = null,
    /**
     * Não dá para saber se o cliente pagou (o app reiniciou ou deu erro no meio e o PagBank não confirmou): o tablet
     * não decide, o operador confere na maquininha.
     */
    val indefinido: Boolean = false,
)

/** Quem cobra de verdade: na maquininha, o PagBank (PlugPag); nos testes, um de mentira. */
interface Pagador {
    fun info(): InfoTerminal

    /** Cobra e só volta no fim (aprovado, recusado ou cancelado). Uma cobrança por vez, sempre na mesma linha. */
    fun pagar(pedido: PedidoPagamento, avisar: (String) -> Unit): ResultadoPagamento

    /** Pede para cancelar a cobrança em andamento (chamado de outra linha enquanto [pagar] espera). */
    fun cancelar()

    /**
     * A última cobrança aprovada nesta maquininha (para quando o app fechou ou deu erro no meio de uma cobrança).
     * Lança exceção se o PagBank não respondeu: aí não dá para saber.
     */
    fun ultimaAprovada(): ResultadoPagamento?
}

/** Onde os resultados ficam guardados: sobrevivem a fechar o app e a desligar a maquininha. */
interface Armazem {
    fun resultado(id: String): ResultadoPagamento?
    fun guardar(id: String, resultado: ResultadoPagamento)
    fun emAndamento(): PedidoPagamento?
    fun marcarEmAndamento(pedido: PedidoPagamento?)
    fun ultimas(quantas: Int): List<Pair<String, ResultadoPagamento>>
}

/** Uma ligação com o tablet (Bluetooth na maquininha; nos testes, um cano na memória). */
interface Ligacao {
    fun enviar(mensagem: Mensagem)
    fun fechar()
}

/**
 * O coração do app: recebe os pedidos do tablet e cobra no PagBank. As regras são as mesmas do programa do tablet:
 * - cada cobrança tem um identificador; o resultado fica guardado (com o valor) e a mesma cobrança nunca é feita
 *   duas vezes; o mesmo identificador com outro valor é recusado;
 * - uma cobrança por vez (outra enquanto uma está em andamento volta "ocupada");
 * - uma ligação por vez: a nova toma o lugar da velha, e o que chega pela velha é ignorado (o tablet já está
 *   perguntando pela nova);
 * - o resultado vai para a ligação que estiver aberta quando a cobrança terminar; se não houver nenhuma, o tablet
 *   pergunta ("consultar") quando voltar;
 * - quando não dá para saber se o cliente pagou, a resposta é "indefinida": quem decide é o operador.
 */
class Ponte(
    private val pagador: Pagador,
    private val armazem: Armazem,
    /** Onde as cobranças rodam (uma linha só: o PagBank não aceita duas ao mesmo tempo). */
    private val cobrancas: Executor,
    /** Onde rodam as outras chamadas ao PagBank (cancelar, perguntar se está ativada). */
    private val outras: Executor,
    private val aoMudar: (Situacao) -> Unit = {},
    /** Ao abrir: quantas vezes pergunta ao PagBank pela cobrança que ficou no meio (o serviço dele demora a subir). */
    private val tentativasRecuperar: Int = 24,
    private val intervaloRecuperar: Long = 5000,
) {
    /** O que aparece na tela do app. */
    data class Situacao(val ligado: Boolean, val caixa: Int?, val cobrando: PedidoPagamento?)

    private val trava = Any()
    private var atual: Ligacao? = null
    private var emAndamento: PedidoPagamento? = null

    /** O tablet pediu para cancelar a cobrança em andamento. */
    private var cancelamentoPedido = false
    private var caixa: Int? = null

    /** Resultados que não deu para gravar no arquivo (erro de disco): valem enquanto o app estiver aberto. */
    private val naoGravados = HashMap<String, ResultadoPagamento>()

    /** A cobrança que estava em andamento quando o app (ou a maquininha) fechou: resolvida por [recuperar]. */
    private var pendenteAoAbrir: PedidoPagamento? = null

    init {
        val pendente = armazem.emAndamento()
        if (pendente != null) {
            if (armazem.resultado(pendente.id) != null) {
                // Terminou e foi guardada; só faltou apagar a marca
                limparMarca()
            } else {
                // Até resolver, ela conta como em andamento: o tablet recebe "andamento" por ela e "ocupada" pelas outras
                emAndamento = pendente
                pendenteAoAbrir = pendente
            }
        }
    }

    val situacao: Situacao get() = synchronized(trava) { Situacao(atual != null, caixa, emAndamento) }

    /**
     * Ao abrir o app: a cobrança que estava em andamento quando o app (ou a maquininha) fechou vira aprovada se o
     * PagBank diz que a última aprovada é ela (mesmo código e mesmo valor). Se não for, ou se o PagBank não responder,
     * fica "indefinida": o cliente pode ter pago (o PagBank termina o pagamento mesmo com este app fechado).
     */
    fun recuperar() {
        val pendente = synchronized(trava) { pendenteAoAbrir.also { pendenteAoAbrir = null } } ?: return
        avisar()
        var resultado: ResultadoPagamento? = null
        var tentativa = 0
        while (resultado == null && tentativa < tentativasRecuperar) {
            if (tentativa > 0) Thread.sleep(intervaloRecuperar)
            tentativa++
            resultado = try {
                conferir(pagador.ultimaAprovada(), pendente,
                    "A cobrança foi interrompida (o app ou a maquininha reiniciou) e não aparece como aprovada no PagBank.")
            } catch (e: Exception) {
                null
            }
        }
        terminar(pendente, resultado ?: indefinido(
            "A cobrança foi interrompida (o app ou a maquininha reiniciou) e o PagBank não respondeu."))
    }

    fun conectou(ligacao: Ligacao) {
        val velha = synchronized(trava) {
            val v = atual
            atual = ligacao
            v
        }
        velha?.fechar()
        avisar()
    }

    fun desconectou(ligacao: Ligacao) {
        synchronized(trava) {
            if (atual !== ligacao) return
            atual = null
        }
        avisar()
    }

    fun recebeu(ligacao: Ligacao, linha: String) {
        val m = Mensagem.ler(linha) ?: return
        if (m.tipo == "ola") {
            // Pergunta ao PagBank se está ativada fora da trava (é uma chamada ao serviço do PagBank)
            executarFora { responderOla(ligacao, m) }
            return
        }
        synchronized(trava) {
            if (ligacao !== atual) return
            when (m.tipo) {
                "ping" -> ligacao.enviar(Mensagem.de("tipo" to "pong"))
                "consultar" -> m.id?.let { responder(ligacao, it, m.valor) }
                "cancelar" -> {
                    // O tablet repete o pedido até a resposta chegar: cada um pede de novo ao PagBank
                    if (m.id != null && m.id == emAndamento?.id) {
                        cancelamentoPedido = true
                        executarFora { pagador.cancelar() }
                    }
                }
                "cobrar" -> cobrar(ligacao, m)
            }
            Unit
        }
    }

    private fun responderOla(ligacao: Ligacao, m: Mensagem) {
        val info = try {
            pagador.info()
        } catch (e: Exception) {
            InfoTerminal("PagBank Moderninha Smart", "", false, "Não consegui falar com o PagBank nesta maquininha.")
        }
        synchronized(trava) {
            if (ligacao !== atual) return
            caixa = m.caixa
            ligacao.enviar(
                Mensagem.de(
                    "tipo" to "ola", "versao" to Mensagem.VERSAO, "maquininha" to info.modelo, "serial" to info.serial,
                    "pronta" to info.pronta, "mensagem" to info.mensagem,
                )
            )
        }
        avisar()
    }

    private fun guardado(id: String): ResultadoPagamento? = naoGravados[id] ?: armazem.resultado(id)

    private fun responder(ligacao: Ligacao, id: String, valor: Long?) {
        val guardado = guardado(id)
        val andando = emAndamento?.takeIf { it.id == id }
        val cobrado = guardado?.valor ?: andando?.valor
        when {
            valor != null && cobrado != null && valor != cobrado -> ligacao.enviar(
                Mensagem.de("tipo" to "erro", "id" to id, "mensagem" to "Este pedido foi cobrado com outro valor na maquininha.")
            )
            guardado != null -> ligacao.enviar(Mensagem.resultado(id, guardado))
            andando != null -> ligacao.enviar(Mensagem.andamento(id, "Pagamento em andamento na maquininha"))
            else -> ligacao.enviar(Mensagem.de("tipo" to "desconhecida", "id" to id))
        }
    }

    private fun cobrar(ligacao: Ligacao, m: Mensagem) {
        val id = m.id
        val valor = m.valor
        val forma = m.forma
        if (id.isNullOrBlank() || valor == null || valor <= 0 || valor > Int.MAX_VALUE || forma == null ||
            forma !in Mensagem.FORMAS
        ) {
            ligacao.enviar(Mensagem.de("tipo" to "erro", "id" to id, "mensagem" to "Cobrança inválida."))
            return
        }
        // Já feita ou fazendo: nunca cobra de novo
        if (guardado(id) != null || emAndamento?.id == id) {
            responder(ligacao, id, valor)
            return
        }
        if (emAndamento != null) {
            ligacao.enviar(
                Mensagem.de("tipo" to "ocupada", "id" to id, "mensagem" to "A maquininha está fazendo outro pagamento.")
            )
            return
        }
        val pedido = PedidoPagamento(id, Mensagem.referenciaLimpa(m.referencia, id), valor, forma, m.comprovante)
        // Gravado antes de começar: se o app fechar no meio, ao abrir ele sabe que havia uma cobrança. Sem conseguir
        // gravar, não cobra.
        try {
            armazem.marcarEmAndamento(pedido)
        } catch (e: Exception) {
            ligacao.enviar(Mensagem.de("tipo" to "erro", "id" to id,
                "mensagem" to "A maquininha não conseguiu guardar a cobrança. Nada foi cobrado."))
            return
        }
        emAndamento = pedido
        cancelamentoPedido = false
        try {
            cobrancas.execute { executar(pedido) }
        } catch (e: Exception) {
            emAndamento = null
            limparMarca()
            ligacao.enviar(Mensagem.de("tipo" to "erro", "id" to id,
                "mensagem" to "A maquininha não conseguiu começar a cobrança. Nada foi cobrado."))
            return
        }
        avisar()
    }

    private fun executar(pedido: PedidoPagamento) {
        val resultado = try {
            if (synchronized(trava) { cancelamentoPedido }) {
                // O tablet cancelou antes de o PagBank começar: nem abre a tela de pagamento
                ResultadoPagamento(aprovado = false, cancelado = true, mensagem = "Pagamento cancelado")
            } else {
                var pediuDeNovo = false
                pagador.pagar(pedido) { texto ->
                    // O cancelamento pode ter chegado antes de o PagBank abrir a tela (não havia o que cancelar)
                    if (!pediuDeNovo && synchronized(trava) { cancelamentoPedido }) {
                        pediuDeNovo = true
                        executarFora { pagador.cancelar() }
                    }
                    enviarAtual(Mensagem.andamento(pedido.id, texto))
                }
            }
        } catch (e: Exception) {
            // Erro no meio: só vale como aprovada se o PagBank disser que a última aprovada é esta. Se não, o
            // cliente pode ter pago: o operador confere.
            try {
                conferir(pagador.ultimaAprovada(), pedido,
                    "A maquininha deu um erro no meio da cobrança e ela não aparece como aprovada no PagBank.")
            } catch (_: Exception) {
                indefinido("A maquininha deu um erro no meio da cobrança e o PagBank não respondeu.")
            }
        }
        terminar(pedido, resultado)
    }

    /** Aprovada só se a última aprovada do PagBank é esta cobrança: mesmo código e (se der para ler) mesmo valor. */
    private fun conferir(ultima: ResultadoPagamento?, pedido: PedidoPagamento, motivo: String): ResultadoPagamento =
        if (ultima != null && ultima.aprovado && ultima.referencia == pedido.referencia &&
            (ultima.valor == null || ultima.valor == pedido.valor)
        ) ultima
        else indefinido(motivo)

    private fun indefinido(motivo: String) = ResultadoPagamento(
        aprovado = false, indefinido = true,
        mensagem = "$motivo Confira na tela da maquininha (ou no app do PagBank) se o pagamento foi aprovado.",
    )

    private fun terminar(pedido: PedidoPagamento, resultado: ResultadoPagamento) {
        val final = resultado.copy(valor = pedido.valor)
        val destino = synchronized(trava) {
            try {
                armazem.guardar(pedido.id, final)
                naoGravados.remove(pedido.id)
            } catch (e: Exception) {
                naoGravados[pedido.id] = final
            }
            limparMarca()
            if (emAndamento?.id == pedido.id) emAndamento = null
            cancelamentoPedido = false
            atual
        }
        destino?.enviar(Mensagem.resultado(pedido.id, final))
        avisar()
    }

    private fun limparMarca() {
        try {
            armazem.marcarEmAndamento(null)
        } catch (_: Exception) {
            // Fica a marca: ao abrir de novo, a cobrança já tem resultado guardado e a marca é apagada
        }
    }

    /** Uma chamada ao PagBank fora da trava e fora da linha das cobranças. Erro ali não derruba nada. */
    private fun executarFora(acao: () -> Unit) {
        try {
            outras.execute {
                try {
                    acao()
                } catch (_: Exception) {
                    // Não deu (ex.: cancelar depois de o pagamento terminar): a cobrança segue o caminho dela
                }
            }
        } catch (_: Exception) {
            // App fechando
        }
    }

    private fun enviarAtual(mensagem: Mensagem) {
        synchronized(trava) { atual }?.enviar(mensagem)
    }

    private fun avisar() = aoMudar(situacao)
}
