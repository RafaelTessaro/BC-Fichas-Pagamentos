package br.com.bcfichas.ponte

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import java.io.File
import java.io.IOException
import java.util.UUID
import java.util.concurrent.Executors
import kotlin.concurrent.thread

/**
 * Fica sempre ligado (com o aviso na barra da maquininha): espera o tablet pelo Bluetooth e repassa as cobranças
 * para o PagBank. Continua funcionando quando a tela do PagBank aparece por cima do app.
 */
class ServicoPonte : Service() {
    private lateinit var ponte: Ponte
    private val servidores = mutableListOf<BluetoothServerSocket>()

    @Volatile
    private var ligado = true

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        startForeground(NOTIFICACAO, notificacao("Esperando o tablet"))
        val armazem = ArmazemArquivo(File(filesDir, "cobrancas.json"))
        ponte = Ponte(
            PagadorPlugPag(applicationContext), armazem, Executors.newSingleThreadExecutor(),
            Executors.newCachedThreadPool(),
        ) { situacao -> mudou(situacao, armazem) }
        instancia = this
        ultimoArmazem = armazem
        // Cobrança que ficou pela metade (app ou maquininha reiniciou): resolve antes de atender o tablet
        thread(name = "ponte-recuperar") {
            ponte.recuperar()
            aceitar(Mensagem.SERVICO, "BC Fichas")
            aceitar(Mensagem.SERIAL_PADRAO, "BC Fichas (porta serial)")
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        ligado = false
        synchronized(servidores) {
            for (s in servidores) try {
                s.close()
            } catch (_: IOException) {
            }
            servidores.clear()
        }
        instancia = null
        super.onDestroy()
    }

    /** Espera ligações num serviço Bluetooth. Se o Bluetooth desligar, espera ele voltar e abre de novo. */
    private fun aceitar(servico: UUID, nome: String) = thread(name = "ponte-$nome", isDaemon = true) {
        while (ligado) {
            val adaptador = getSystemService(BluetoothManager::class.java)?.adapter
            if (adaptador == null || !adaptador.isEnabled) {
                Thread.sleep(3000)
                continue
            }
            try {
                val servidor = adaptador.listenUsingRfcommWithServiceRecord(nome, servico)
                synchronized(servidores) { servidores += servidor }
                while (ligado) {
                    val socket = servidor.accept()
                    val ligacao = LigacaoFluxo(socket.inputStream, socket.outputStream, socket, ponte)
                    ponte.conectou(ligacao)
                    ligacao.iniciar()
                }
            } catch (e: IOException) {
                if (ligado) Thread.sleep(2000)
            }
        }
    }

    private fun mudou(situacao: Ponte.Situacao, armazem: Armazem) {
        val texto = when {
            situacao.cobrando != null -> "Cobrando ${Dinheiro.formatar(situacao.cobrando.valor)}"
            situacao.ligado -> "Ligada ao tablet" + (situacao.caixa?.let { " (Caixa %02d)".format(it) } ?: "")
            else -> "Esperando o tablet"
        }
        situacaoAtual = texto
        getSystemService(NotificationManager::class.java)?.notify(NOTIFICACAO, notificacao(texto))
        aoMudar?.invoke(texto, armazem)
    }

    private fun notificacao(texto: String): Notification {
        val abrir = PendingIntent.getActivity(
            this, 0, Intent(this, TelaPrincipal::class.java),
            if (Build.VERSION.SDK_INT >= 23) PendingIntent.FLAG_IMMUTABLE else 0,
        )
        val construtor = if (Build.VERSION.SDK_INT >= 26) {
            val gerente = getSystemService(NotificationManager::class.java)
            gerente?.createNotificationChannel(NotificationChannel(CANAL, "BC Fichas", NotificationManager.IMPORTANCE_LOW))
            Notification.Builder(this, CANAL)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
        }
        return construtor.setContentTitle("BC Fichas")
            .setContentText(texto)
            .setSmallIcon(R.drawable.icone_barra)
            .setContentIntent(abrir)
            .setOngoing(true)
            .build()
    }

    companion object {
        private const val NOTIFICACAO = 1
        private const val CANAL = "ponte"

        @Volatile
        var situacaoAtual: String = "Iniciando..."
            private set

        @Volatile
        var ultimoArmazem: Armazem? = null
            private set

        @Volatile
        private var instancia: ServicoPonte? = null

        /** A tela do app acompanha o que acontece (chamado fora da linha da tela). */
        @Volatile
        var aoMudar: ((String, Armazem) -> Unit)? = null

        fun iniciar(context: Context) {
            context.startService(Intent(context, ServicoPonte::class.java))
        }
    }
}

object Dinheiro {
    fun formatar(centavos: Long): String {
        val reais = centavos / 100
        val milhares = reais.toString().reversed().chunked(3).joinToString(".").reversed()
        return "R$ $milhares,%02d".format(centavos % 100)
    }
}
