/* Windows loopback socket keeper. No managed callbacks: survives domain reload.
 * Synchronization primitives live for the DLL lifetime. Client handles include a
 * generation; retired slots cannot be reused until all outstanding I/O releases.
 * Build with the adjacent build.py (pinned MSVC x64 and Windows SDK). */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdlib.h>
#include <stdio.h>
#include <limits.h>
#pragma comment(lib, "ws2_32.lib")
#define MAX_CLIENTS 64
#define API __declspec(dllexport)

typedef struct {
    SOCKET socket;
    int handle, live, refs;
    char *staged;
    int staged_len;
} Client;
static Client clients[MAX_CLIENTS];
static CRITICAL_SECTION client_gate, queue_gate;
static INIT_ONCE once = INIT_ONCE_STATIC_INIT;
static HANDLE queue_event, accept_thread;
static SOCKET listener = INVALID_SOCKET;
static volatile LONG running, attached;
static int port_number, next_generation = 1;
static int queue[MAX_CLIENTS], head, tail, count;

static BOOL CALLBACK Initialize(PINIT_ONCE init, PVOID parameter, PVOID *context)
{
    WSADATA wsa;
    (void)init; (void)parameter; (void)context;
    if (WSAStartup(MAKEWORD(2,2), &wsa)) return FALSE;
    InitializeCriticalSection(&client_gate);
    InitializeCriticalSection(&queue_gate);
    queue_event = CreateEventW(NULL, FALSE, FALSE, NULL);
    for (int i=0; i<MAX_CLIENTS; i++) clients[i].socket = INVALID_SOCKET;
    return queue_event != NULL;
}

static Client *Find(int handle)
{
    if (handle < 0) return NULL;
    Client *c = &clients[handle % MAX_CLIENTS];
    return c->handle == handle ? c : NULL;
}

static void Finalize(Client *c)
{
    if (c->live || c->refs) return;
    if (c->socket != INVALID_SOCKET) closesocket(c->socket);
    c->socket = INVALID_SOCKET;
    free(c->staged); c->staged = NULL; c->staged_len = 0;
}

static SOCKET Acquire(int handle)
{
    SOCKET s = INVALID_SOCKET;
    EnterCriticalSection(&client_gate);
    Client *c = Find(handle);
    if (c && c->live) { c->refs++; s = c->socket; }
    LeaveCriticalSection(&client_gate);
    return s;
}

static void Release(int handle)
{
    EnterCriticalSection(&client_gate);
    Client *c = Find(handle);
    if (c && c->refs) { c->refs--; Finalize(c); }
    LeaveCriticalSection(&client_gate);
}

API void __cdecl mcpkeeper_close_client(int handle)
{
    EnterCriticalSection(&client_gate);
    Client *c = Find(handle);
    if (c && c->live) {
        c->live = 0;
        shutdown(c->socket, SD_BOTH); /* Interrupt an in-progress recv before retirement. */
        Finalize(c);
    }
    LeaveCriticalSection(&client_gate);
}

static int Allocate(SOCKET socket)
{
    int handle = -1;
    EnterCriticalSection(&client_gate);
    for (int i=0; i<MAX_CLIENTS; i++) {
        Client *c = &clients[i];
        if (!c->live && !c->refs && c->socket == INVALID_SOCKET) {
            /* Never wrap: an exhausted handle space rejects new clients until process restart. */
            if (next_generation >= INT_MAX / MAX_CLIENTS) break;
            handle = next_generation++ * MAX_CLIENTS + i;
            c->handle = handle; c->socket = socket; c->live = 1;
            break;
        }
    }
    LeaveCriticalSection(&client_gate);
    return handle;
}

static int SendAll(SOCKET s, const char *data, int length)
{
    ULONGLONG deadline = GetTickCount64() + 1000;
    int sent = 0;
    while (sent < length && GetTickCount64() < deadline) {
        int n = send(s, data + sent, length - sent, 0);
        if (n <= 0) return -1;
        sent += n;
    }
    return sent == length ? sent : -1;
}

static void Reject(SOCKET s)
{
    /* Bound header draining by wall time; idle clients must not stall reload. */
    char buffer[8192];
    fd_set reads; FD_ZERO(&reads); FD_SET(s, &reads);
    struct timeval wait = { 0, 30000 };
    if (select(0, &reads, NULL, NULL, &wait) > 0) recv(s, buffer, sizeof(buffer), 0);
    const char *body = "{\"error_code\":\"server_unavailable\",\"error_message\":\"Unity is reloading or the server is busy. Retry after the delay.\",\"dispatch_state\":\"not_started\"}";
    char response[512];
    int length = snprintf(response, sizeof(response),
        "HTTP/1.1 503 Service Unavailable\r\nRetry-After: 3\r\nContent-Type: application/json\r\nContent-Length: %u\r\nConnection: close\r\n\r\n%s",
        (unsigned)strlen(body), body);
    SendAll(s, response, length);
    shutdown(s, SD_SEND);
}

static int Pop(void)
{
    int handle = -1;
    EnterCriticalSection(&queue_gate);
    if (count) { handle = queue[head]; head = (head+1)%MAX_CLIENTS; count--; }
    LeaveCriticalSection(&queue_gate);
    return handle;
}

static DWORD WINAPI AcceptLoop(LPVOID parameter)
{
    (void)parameter;
    while (InterlockedCompareExchange(&running,0,0)) {
        if (!InterlockedCompareExchange(&attached,0,0)) {
            int handle = Pop();
            if (handle >= 0) {
                SOCKET s = Acquire(handle);
                if (s != INVALID_SOCKET) { Reject(s); Release(handle); }
                mcpkeeper_close_client(handle);
                continue;
            }
        }
        SOCKET listen_socket = listener;
        if (listen_socket == INVALID_SOCKET) break;
        fd_set reads; FD_ZERO(&reads); FD_SET(listen_socket,&reads);
        struct timeval wait = {0,50000};
        if (select(0,&reads,NULL,NULL,&wait) <= 0) continue;
        SOCKET s = accept(listen_socket,NULL,NULL);
        if (s == INVALID_SOCKET) continue;
        if (!SetHandleInformation((HANDLE)s, HANDLE_FLAG_INHERIT, 0)) { closesocket(s); continue; }
        int yes=1; DWORD receive_timeout=5000, send_timeout=100;
        setsockopt(s,IPPROTO_TCP,TCP_NODELAY,(const char*)&yes,sizeof(yes));
        setsockopt(s,SOL_SOCKET,SO_RCVTIMEO,(const char*)&receive_timeout,sizeof(receive_timeout));
        setsockopt(s,SOL_SOCKET,SO_SNDTIMEO,(const char*)&send_timeout,sizeof(send_timeout));
        int handle=Allocate(s), queued=0;
        EnterCriticalSection(&queue_gate);
        if (handle >= 0 && attached && running && count < MAX_CLIENTS) {
            queue[tail]=handle; tail=(tail+1)%MAX_CLIENTS; count++; queued=1;
        }
        LeaveCriticalSection(&queue_gate);
        if (queued) SetEvent(queue_event);
        else {
            if (handle >= 0) {
                SOCKET held=Acquire(handle);
                if (held != INVALID_SOCKET) { Reject(held); Release(handle); }
                mcpkeeper_close_client(handle);
            } else { Reject(s); closesocket(s); }
        }
    }
    return 0;
}

API int __cdecl mcpkeeper_start(int port)
{
    if (!InitOnceExecuteOnce(&once,Initialize,NULL,NULL)) return -1;
    if (InterlockedCompareExchange(&running,0,0)) return 0;
    if (port < 1 || port > 65535) return -2;
    /* Unity spawns import/compiler children. An inherited listener stays bound even
     * after this provider stops, with no accept thread to service it. Disable
     * inheritance atomically at creation, before any child can duplicate it. */
    SOCKET s=WSASocketW(AF_INET,SOCK_STREAM,IPPROTO_TCP,NULL,0,
        WSA_FLAG_OVERLAPPED | WSA_FLAG_NO_HANDLE_INHERIT);
    if (s == INVALID_SOCKET) return -3;
    int exclusive=1;
    setsockopt(s,SOL_SOCKET,SO_EXCLUSIVEADDRUSE,(const char*)&exclusive,sizeof(exclusive));
    struct sockaddr_in address; memset(&address,0,sizeof(address));
    address.sin_family=AF_INET; address.sin_addr.s_addr=htonl(INADDR_LOOPBACK); address.sin_port=htons((u_short)port);
    if (bind(s,(struct sockaddr*)&address,sizeof(address)) == SOCKET_ERROR || listen(s,SOMAXCONN) == SOCKET_ERROR) { closesocket(s); return -4; }
    EnterCriticalSection(&queue_gate); head=tail=count=0; LeaveCriticalSection(&queue_gate);
    ResetEvent(queue_event); listener=s; port_number=port;
    InterlockedExchange(&attached,0); InterlockedExchange(&running,1);
    accept_thread=CreateThread(NULL,0,AcceptLoop,NULL,0,NULL);
    if (!accept_thread) { InterlockedExchange(&running,0); closesocket(s); listener=INVALID_SOCKET; port_number=0; return -5; }
    return 0;
}
API int __cdecl mcpkeeper_is_running(void) { return (int)InterlockedCompareExchange(&running,0,0); }
API int __cdecl mcpkeeper_get_port(void) { return port_number; }
API void __cdecl mcpkeeper_attach(void) { InterlockedExchange(&attached,1); }
API void __cdecl mcpkeeper_detach(void)
{
    EnterCriticalSection(&queue_gate); InterlockedExchange(&attached,0); LeaveCriticalSection(&queue_gate);
    SetEvent(queue_event); /* Native thread rejects queued requests; no managed-thread socket waits. */
}
API int __cdecl mcpkeeper_dequeue(int timeout_ms)
{
    ULONGLONG deadline=GetTickCount64()+(DWORD)(timeout_ms < 0 ? 0 : timeout_ms);
    while (attached && running) {
        int handle=Pop(); if (handle >= 0) return handle;
        ULONGLONG now=GetTickCount64(); if (now >= deadline) break;
        WaitForSingleObject(queue_event,(DWORD)(deadline-now));
    }
    return -1;
}
API int __cdecl mcpkeeper_recv(int handle,char *buffer,int length)
{
    SOCKET s=Acquire(handle); if (s == INVALID_SOCKET) return -1;
    int result=-1;
    ULONGLONG deadline=GetTickCount64()+5000;
    while (GetTickCount64()<deadline) {
        EnterCriticalSection(&client_gate); Client *c=Find(handle); int live=c && c->live; LeaveCriticalSection(&client_gate);
        if (!live) break;
        fd_set reads; FD_ZERO(&reads); FD_SET(s,&reads); struct timeval wait={0,50000};
        int ready=select(0,&reads,NULL,NULL,&wait);
        if (ready<0) break;
        if (ready>0) { result=recv(s,buffer,length,0); break; }
    }
    Release(handle); return result;
}
API int __cdecl mcpkeeper_send(int handle,const char *buffer,int length)
{
    SOCKET s=Acquire(handle); if (s == INVALID_SOCKET) return -1;
    int result=SendAll(s,buffer,length); Release(handle); return result;
}
API int __cdecl mcpkeeper_is_client_connected(int handle)
{
    SOCKET s=Acquire(handle); if (s == INVALID_SOCKET) return 0;
    fd_set reads; FD_ZERO(&reads); FD_SET(s,&reads); struct timeval wait={0,0};
    int ready=select(0,&reads,NULL,NULL,&wait), connected=1;
    if (ready < 0) connected=0;
    else if (ready > 0) { char byte; connected=recv(s,&byte,1,MSG_PEEK)>0; }
    Release(handle); return connected;
}
API void __cdecl mcpkeeper_stage_response(int handle,const char *data,int length)
{
    if (!data || length <= 0) return;
    EnterCriticalSection(&client_gate); Client *c=Find(handle);
    if (c && c->live) {
        char *copy=(char*)malloc((size_t)length);
        if (copy) { memcpy(copy,data,(size_t)length); free(c->staged); c->staged=copy; c->staged_len=length; }
    }
    LeaveCriticalSection(&client_gate);
}
API void __cdecl mcpkeeper_flush_staged(void)
{
    for (int i=0;i<MAX_CLIENTS;i++) {
        EnterCriticalSection(&client_gate); Client *c=&clients[i];
        char *data=NULL; int length=0,handle=-1;
        if (c->live && c->staged) { data=c->staged; length=c->staged_len; handle=c->handle; c->staged=NULL; c->staged_len=0; }
        LeaveCriticalSection(&client_gate);
        if (data) { mcpkeeper_send(handle,data,length); free(data); mcpkeeper_close_client(handle); }
    }
}
API void __cdecl mcpkeeper_stop(void)
{
    if (!InterlockedExchange(&running,0)) return;
    InterlockedExchange(&attached,0); SetEvent(queue_event);
    SOCKET s=listener; listener=INVALID_SOCKET; if (s != INVALID_SOCKET) closesocket(s);
    for (int i=0;i<MAX_CLIENTS;i++) {
        EnterCriticalSection(&client_gate); int handle=clients[i].handle; LeaveCriticalSection(&client_gate);
        mcpkeeper_close_client(handle);
    }
    if (accept_thread) { WaitForSingleObject(accept_thread,INFINITE); CloseHandle(accept_thread); accept_thread=NULL; }
    EnterCriticalSection(&queue_gate); head=tail=count=0; LeaveCriticalSection(&queue_gate);
    port_number=0;
}
