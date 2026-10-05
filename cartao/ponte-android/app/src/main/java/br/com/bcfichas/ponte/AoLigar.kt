package br.com.bcfichas.ponte

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** A maquininha ligou: o app já fica esperando o tablet, sem ninguém precisar abrir. */
class AoLigar : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == Intent.ACTION_BOOT_COMPLETED) ServicoPonte.iniciar(context)
    }
}
