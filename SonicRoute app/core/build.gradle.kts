plugins {
    kotlin("jvm")
    `java-library`
}

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

kotlin {
    compilerOptions.jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
}

val checkPermissionState by tasks.registering(JavaExec::class) {
    group = "verification"
    description = "Checks permission state, capability revision, lease & audio math on the JVM."
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.PermissionStateCheckKt")
}

val checkSrapVectors by tasks.registering(JavaExec::class) {
    group = "verification"
    description = "Checks SRAP v1.0 golden framing & strict JSON vectors independently on the JVM."
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.SrapVectorCheckKt")
    val vectors = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-v1.txt")
    inputs.file(vectors)
    args(vectors.asFile.absolutePath)
}

val checkMediaTransport by tasks.registering(JavaExec::class) {
    group = "verification"
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.MediaTransportCheckKt")
    val vectors = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-pcm-v1.txt")
    val mediaState = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-media-state-v1.txt")
    inputs.files(vectors, mediaState)
    args(vectors.asFile.absolutePath, mediaState.asFile.absolutePath)
}

tasks.check { dependsOn(checkMediaTransport) }

val checkSrapTlsIntegration by tasks.registering(JavaExec::class) {
    group = "verification"
    description = "Checks SRAP v1.0 TLS 1.3/1.2 pairing, mTLS, dispatch, capability sync, revocation & cleanup."
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.SrapTlsIntegrationCheckKt")
    val vectors = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-business-v1.txt")
    inputs.file(vectors)
    args(vectors.asFile.absolutePath)
}

val checkSrapAliasIntegration by tasks.registering(JavaExec::class) {
    group = "verification"
    description = "Checks SRAP alias transcripts, real loopback mTLS, cleanup, invitations and durable trust."
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.SrapAliasIntegrationCheckKt")
    val vectors = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-alias-v1.txt")
    inputs.file(vectors)
    args(vectors.asFile.absolutePath)
}

val checkSrapClipboard by tasks.registering(JavaExec::class) {
    group = "verification"
    description = "Checks SRAP clipboard data, scoped watches, revision conflicts and readback across two sessions."
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.SrapClipboardCheckKt")
    val vectors = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-clipboard-v1.txt")
    inputs.file(vectors)
    args(vectors.asFile.absolutePath)
}

val checkConnectionLifecycle by tasks.registering(JavaExec::class) {
    group = "verification"
    description = "Checks 4-layer lifecycle separation, Wi-Fi recovery, power gating & stop semantics."
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.ConnectionLifecycleCheckKt")
}

val checkNativeAppRouteEvidence by tasks.registering(JavaExec::class) {
    group = "verification"
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.NativeAppRouteEvidenceCheckKt")
}
tasks.check { dependsOn(checkNativeAppRouteEvidence) }

val checkLocalVolumeRules by tasks.registering(JavaExec::class) {
    group = "verification"
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.LocalVolumeRulesCheckKt")
}

tasks.check { dependsOn(checkLocalVolumeRules) }

val checkSrapFiles by tasks.registering(JavaExec::class) {
    group = "verification"
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.SrapFileCheckKt")
    val vectors = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-business-v1.txt")
    inputs.file(vectors)
    args(vectors.asFile.absolutePath)
}
tasks.check { dependsOn(checkSrapFiles) }

val checkSrapDisplay by tasks.registering(JavaExec::class) {
    group = "verification"
    classpath = sourceSets["test"].runtimeClasspath
    mainClass.set("org.sonicroute.core.SrapDisplayCheckKt")
    val vectors = rootProject.layout.projectDirectory.file("docs/protocol-fixtures/srap-business-v1.txt")
    inputs.file(vectors)
    args(vectors.asFile.absolutePath)
}
tasks.check { dependsOn(checkSrapDisplay) }

tasks.check {
    dependsOn(checkPermissionState, checkSrapVectors, checkSrapTlsIntegration, checkSrapAliasIntegration, checkSrapClipboard, checkConnectionLifecycle)
}
