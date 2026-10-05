package br.com.bcfichas.ponte

import android.content.Context
import br.com.uol.pagseguro.plugpagservice.wrapper.PlugPag
import br.com.uol.pagseguro.plugpagservice.wrapper.PlugPagEventData
import br.com.uol.pagseguro.plugpagservice.wrapper.PlugPagEventListener
import br.com.uol.pagseguro.plugpagservice.wrapper.PlugPagPaymentData
import br.com.uol.pagseguro.plugpagservice.wrapper.PlugPagTransactionResult

/**
 * Cobra pelo PagBank que já vem na maquininha (PlugPagServiceWrapper). O PagBank mostra as telas dele (cartão, senha,
 * QR Code do PIX) por cima deste app; aqui só se pede o valor e se espera o resultado.
 *
 * Um PlugPag só para o app inteiro, e nunca duas chamadas de cobrança ao mesmo tempo (regras do PagBank).
 */
class PagadorPlugPag(context: Context) : Pagador {
    private val plugPag = PlugPag(context)

    override fun info(): InfoTerminal {
        val pronta = try {
            plugPag.isAuthenticated()
        } catch (e: Exception) {
            false
        }
        val modelo = try {
            plugPag.getModel()
        } catch (e: Exception) {
            null
        }
        val serial = try {
            plugPag.getSerialNumber()
        } catch (e: Exception) {
            null
        }
        return InfoTerminal(
            modelo = "PagBank Moderninha Smart" + if (modelo.isNullOrBlank()) "" else " ($modelo)",
            serial = serial ?: "",
            pronta = pronta,
            mensagem = if (pronta) "" else "A maquininha não está ativada no PagBank. Abra o app do PagBank nela e faça a ativação.",
        )
    }

    override fun pagar(pedido: PedidoPagamento, avisar: (String) -> Unit): ResultadoPagamento {
        plugPag.setEventListener(object : PlugPagEventListener {
            override fun onEvent(data: PlugPagEventData) {
                texto(data)?.let(avisar)
            }
        })
        val tipo = when (pedido.forma) {
            "debito" -> PlugPag.TYPE_DEBITO
            "credito" -> PlugPag.TYPE_CREDITO
            else -> PlugPag.TYPE_PIX
        }
        // Crédito à vista, uma parcela. "comprovante" = imprimir a via do estabelecimento
        val dados = PlugPagPaymentData(tipo, pedido.valor.toInt(), PlugPag.INSTALLMENT_TYPE_A_VISTA, 1, pedido.referencia,
            pedido.comprovante)
        return converter(plugPag.doPayment(dados))
    }

    override fun cancelar() {
        plugPag.abort()
    }

    override fun ultimaAprovada(): ResultadoPagamento? {
        val r = plugPag.getLastApprovedTransaction()
        // Sem resposta boa do PagBank não dá para saber: quem chamou trata como "não sei" (nunca como recusada)
        if (r.result != PlugPag.RET_OK)
            throw IllegalStateException("O PagBank não informou a última venda aprovada (código ${r.errorCode ?: r.result}).")
        return converter(r)
    }

    private fun converter(r: PlugPagTransactionResult): ResultadoPagamento {
        val aprovado = r.result == PlugPag.RET_OK
        return ResultadoPagamento(
            aprovado = aprovado,
            cancelado = !aprovado && r.result == PlugPag.OPERATION_ABORTED,
            mensagem = r.message?.takeIf { it.isNotBlank() }
                ?: if (aprovado) "Pagamento aprovado" else "Pagamento não aprovado (código ${r.errorCode ?: r.result})",
            autorizacao = r.autoCode,
            nsu = r.hostNsu ?: r.nsu,
            bandeira = r.cardBrand,
            codigo = r.transactionCode,
            referencia = r.userReference,
            valor = Mensagem.centavos(r.amount),
        )
    }

    /** O que mostrar no tablet enquanto o cliente paga na maquininha. */
    private fun texto(data: PlugPagEventData): String? {
        val personalizado = data.customMessage.ifBlank { null }
        return when (data.eventCode) {
            PlugPagEventData.EVENT_CODE_WAITING_CARD, PlugPagEventData.EVENT_CODE_WATITING_CARD_NO_CTLLS ->
                "Aproxime, insira ou passe o cartão na maquininha"
            PlugPagEventData.EVENT_CODE_INSERTED_CARD -> "Cartão inserido"
            PlugPagEventData.EVENT_CODE_PIN_REQUESTED, PlugPagEventData.EVENT_CODE_DIGIT_PASSWORD ->
                "O cliente está digitando a senha"
            PlugPagEventData.EVENT_CODE_PIN_OK -> "Senha confirmada"
            PlugPagEventData.EVENT_CODE_AUTHORIZING -> "Autorizando..."
            PlugPagEventData.EVENT_CODE_SALE_APPROVED -> "Aprovado"
            PlugPagEventData.EVENT_CODE_SALE_NOT_APPROVED -> "Não aprovado"
            PlugPagEventData.EVENT_CODE_WAITING_REMOVE_CARD -> "Retire o cartão"
            PlugPagEventData.EVENT_CODE_QRCODE, PlugPagEventData.EVENT_CODE_QRCODE_SHOWED ->
                "QR Code do PIX na tela da maquininha: o cliente paga pelo app do banco"
            PlugPagEventData.EVENT_CODE_USE_CHIP -> "Use o chip do cartão"
            PlugPagEventData.EVENT_CODE_USE_TARJA -> "Passe a tarja do cartão"
            PlugPagEventData.EVENT_CODE_CONTACTLESS_ERROR -> "Não leu por aproximação: insira o cartão"
            PlugPagEventData.EVENT_CODE_DOWNLOADING_TABLES, PlugPagEventData.EVENT_CODE_RECORDING_TABLES ->
                "A maquininha está se atualizando..."
            PlugPagEventData.EVENT_CODE_SOLVE_PENDINGS -> "A maquininha está resolvendo pendências..."
            else -> personalizado
        }
    }
}
