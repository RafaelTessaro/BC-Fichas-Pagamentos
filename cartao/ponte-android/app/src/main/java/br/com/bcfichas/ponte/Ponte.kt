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
)

/** Quem cobra de verdade: na maquininha, o PagBank (PlugPag); nos testes, um de mentira. */
interface Pagador {
    fun info(): InfoTerminal

    /** Cobra e só volta no fim (aprovado, recusado ou cancelado). Uma cobrança por vez, sempre na mesma linha. */
    fun pagar(pedido: PedidoPagamento, avisar: (String) -> Unit): ResultadoPagamento

    /** Pede para cancelar a cobrança em andamento (chamado de outra linha enquanto [pagar] espera). */
    fun cancelar()

    /** A última cobrança aprovada nesta maquininha (para quando o app fechou no meio de uma cobrança). */
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
 * - cada cobrança tem um identificador; o resultado fica guardado e a mesma cobrança nunca é feita duas vezes;
 * - uma cobrança por vez (outra enquanto uma está em andamento volta "ocupada");
 * - uma ligação por vez: a nova toma o lugar da velha, e o que chega pela velha é ignorado (o tablet já está
 *   perguntando pela nova);
 * - o resultado vai para a ligação que estiver aberta quando a cobrança terminar; se não houver nenhuma, o tablet
 *   pergunta ("consultar") quando voltar.
 */
class Ponte(
    private val pagador: Pagador,
    private val armazem: Armazem,
    /** Onde as cobranças rodam (uma linha só: o PagBank não aceita duas ao mesmo tempo). */
    private val cobrancas: Executor,
    /** Onde rodam as outras chamadas ao PagBank (cancelar, perguntar se está ativada). */
    private val outras: Executor,
    private val aoMudar: (Situacao) -> Unit = {},
) {
    /** O que aparece na tela do app. */
    data class Situacao(val ligado: Boolean, val caixa: Int?, val cobrando: PedidoPagamento?)

    private val trava = Any()
    private var atual: Ligacao? = null
    private var emAndamento: PedidoPagamento? = null
    private var caixa: Int? = null

    val situacao: Situacao get() = synchronized(trava) { Situacao(atual != null, caixa, emAndamento) }

    /**
     * Ao abrir o app: a cobrança que estava em andamento quando o app (ou a maquininha) fechou vira aprovada se o
     * PagBank diz que a última aprovada é ela, e não aprovada se não.
     */
    fun recuperar() {
        val pendente = synchronized(trava) { if (emAndamento == null) armazem.emAndamento() else null } ?: return
        val ultima = try {
            pagador.ultimaAprovada()
        } catch (e: Exception) {
            null
        }
        val resultado = if (ultima != null && ultima.referencia == pendente.referencia) ultima
        else ResultadoPagamento(
            aprovado = false,
            mensagem = "A cobrança foi interrompida (o app ou a maquininha reiniciou) e não aparece como aprovada.",
        )
        synchronized(trava) {
            armazem.guardar(pendente.id, resultado)
            armazem.marcarEmAndamento(null)
        }
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
            outras.execute { responderOla(ligacao, m) }
            return
        }
        synchronized(trava) {
            if (ligacao !== atual) return
            when (m.tipo) {
                "ping" -> ligacao.enviar(Mensagem.de("tipo" to "pong"))
                "consultar" -> m.id?.let { responder(ligacao, it) }
                "cancelar" -> {
                    if (m.id != null && m.id == emAndamento?.id) outras.execute {
                        try {
                            pagador.cancelar()
                        } catch (e: Exception) {
                            // Não deu para cancelar: a cobrança termina sozinha (aprovada ou não)
                        }
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

    private fun responder(ligacao: Ligacao, id: String) {
        val guardado = armazem.resultado(id)
        when {
            guardado != null -> ligacao.enviar(Mensagem.resultado(id, guardado))
            emAndamento?.id == id -> ligacao.enviar(Mensagem.andamento(id, "Pagamento em andamento na maquininha"))
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
        if (armazem.resultado(id) != null || emAndamento?.id == id) {
            responder(ligacao, id)
            return
        }
        if (emAndamento != null) {
            ligacao.enviar(
                Mensagem.de("tipo" to "ocupada", "id" to id, "mensagem" to "A maquininha está fazendo outro pagamento.")
            )
            return
        }
        val pedido = PedidoPagamento(id, Mensagem.referenciaLimpa(m.referencia, id), valor, forma, m.comprovante)
        emAndamento = pedido
        // Gravado antes de começar: se o app fechar no meio, ao abrir ele sabe que havia uma cobrança
        armazem.marcarEmAndamento(pedido)
        cobrancas.execute { executar(pedido) }
        avisar()
    }

    private fun executar(pedido: PedidoPagamento) {
        val resultado = try {
            pagador.pagar(pedido) { texto -> enviarAtual(Mensagem.andamento(pedido.id, texto)) }
        } catch (e: Exception) {
            // Erro no meio: só vale como aprovada se o PagBank disser que esta foi a última aprovada
            val ultima = try {
                pagador.ultimaAprovada()
            } catch (_: Exception) {
                null
            }
            if (ultima != null && ultima.referencia == pedido.referencia) ultima
            else ResultadoPagamento(aprovado = false, mensagem = "A maquininha não conseguiu cobrar: ${e.message ?: "erro"}")
        }
        val destino = synchronized(trava) {
            armazem.guardar(pedido.id, resultado)
            armazem.marcarEmAndamento(null)
            emAndamento = null
            atual
        }
        destino?.enviar(Mensagem.resultado(pedido.id, resultado))
        avisar()
    }

    private fun enviarAtual(mensagem: Mensagem) {
        synchronized(trava) { atual }?.enviar(mensagem)
    }

    private fun avisar() = aoMudar(situacao)
}
