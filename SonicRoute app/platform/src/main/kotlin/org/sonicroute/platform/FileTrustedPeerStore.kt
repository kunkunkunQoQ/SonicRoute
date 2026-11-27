package org.sonicroute.platform

import android.content.Context
import org.sonicroute.core.SrapPeerFileStore
import org.sonicroute.core.TrustedPeerRegistry

class FileTrustedPeerStore(context: Context) :
    TrustedPeerRegistry by SrapPeerFileStore(context.applicationContext.filesDir)
