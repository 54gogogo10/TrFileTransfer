// C wrapper for UDT4 — exports udt_ prefixed functions for P/Invoke
#include "udt.h"
using namespace UDT;

extern "C" {

__declspec(dllexport) int udt_startup() { return UDT::startup(); }
__declspec(dllexport) int udt_cleanup() { return UDT::cleanup(); }
__declspec(dllexport) int udt_socket(int af, int type, int protocol) { return UDT::socket(af, type, protocol); }
__declspec(dllexport) int udt_bind(int u, const struct sockaddr* name, int namelen) { return UDT::bind(u, name, namelen); }
__declspec(dllexport) int udt_listen(int u, int backlog) { return UDT::listen(u, backlog); }
__declspec(dllexport) int udt_accept(int u, struct sockaddr* addr, int* addrlen) { return UDT::accept(u, addr, addrlen); }
__declspec(dllexport) int udt_connect(int u, const struct sockaddr* name, int namelen) { return UDT::connect(u, name, namelen); }
__declspec(dllexport) int udt_close(int u) { return UDT::close(u); }
__declspec(dllexport) int udt_send(int u, const char* buf, int len, int flags) { return UDT::send(u, buf, len, flags); }
__declspec(dllexport) int udt_recv(int u, char* buf, int len, int flags) { return UDT::recv(u, buf, len, flags); }
__declspec(dllexport) int udt_getlasterror_code() { return UDT::getlasterror_code(); }
__declspec(dllexport) const char* udt_getlasterror_desc() { return UDT::getlasterror_desc(); }
__declspec(dllexport) int udt_setsockopt(int u, int level, int optname, const void* optval, int optlen) { return UDT::setsockopt(u, level, UDT::SOCKOPT(optname), optval, optlen); }
__declspec(dllexport) int udt_getsockopt(int u, int level, int optname, void* optval, int* optlen) { return UDT::getsockopt(u, level, UDT::SOCKOPT(optname), optval, optlen); }

}
