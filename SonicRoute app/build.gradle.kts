plugins {
    id("com.android.application") version "8.7.3" apply false
    id("com.android.library") version "8.7.3" apply false
    kotlin("android") version "2.0.21" apply false
    kotlin("jvm") version "2.0.21" apply false
}

val androidCompileSdk by extra(35)
val androidMinSdk by extra(29)
val androidBuildTools by extra("35.0.1")
