package com.samevoice.mobile

import android.os.Build
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

object NetUtil {
    fun localIpv4(): List<String> {
        val result = linkedSetOf<String>()
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces() ?: return emptyList()
            for (nif in interfaces) {
                if (!nif.isUp || nif.isLoopback) continue
                for (address in nif.inetAddresses) {
                    if (address is Inet4Address && !address.isLoopbackAddress) {
                        result.add(address.hostAddress ?: continue)
                    }
                }
            }
        } catch (_: Exception) {
        }
        return result.toList()
    }

    fun localAddressText(): String =
        localIpv4().takeIf { it.isNotEmpty() }?.joinToString(" · ") ?: "未连接局域网"

    fun broadcastAddresses(): List<InetAddress> {
        val result = linkedSetOf<InetAddress>()
        try {
            val interfaces = NetworkInterface.getNetworkInterfaces()
            if (interfaces != null) {
                for (nif in interfaces) {
                    if (!nif.isUp || nif.isLoopback) continue
                    for (interfaceAddress in nif.interfaceAddresses) {
                        val broadcast = interfaceAddress.broadcast ?: continue
                        if (broadcast is Inet4Address) result.add(broadcast)
                    }
                }
            }
        } catch (_: Exception) {
        }
        try { result.add(InetAddress.getByName("255.255.255.255")) } catch (_: Exception) {}
        return result.toList()
    }

    fun defaultDeviceName(): String {
        val model = Build.MODEL?.trim().orEmpty()
        return if (model.isEmpty()) "我的手机" else model
    }
}
