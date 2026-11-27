plugins {
    id("com.android.library")
    kotlin("android")
}

android {
    namespace = "org.sonicroute.platform"
    compileSdk = rootProject.extra["androidCompileSdk"] as Int
    buildToolsVersion = rootProject.extra["androidBuildTools"] as String
    defaultConfig { minSdk = rootProject.extra["androidMinSdk"] as Int }
    buildFeatures {
        aidl = true
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
}

dependencies {
    api(project(":core"))
    implementation("dev.rikka.shizuku:api:13.1.5")
    implementation("dev.rikka.shizuku:provider:13.1.5")
}
