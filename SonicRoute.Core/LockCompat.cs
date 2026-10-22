#if NET9_0_OR_GREATER
global using LockType = System.Threading.Lock;
#else
global using LockType = object;
#endif