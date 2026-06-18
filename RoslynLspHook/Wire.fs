module RoslynLspHook.Wire

open System.IO
open System.Text
open System.Threading
open RoslynLspHook.Lsp

// Content-Length framed message IO over an arbitrary duplex stream. Shared by the
// broker (its Roslyn child's stdio) and the broker client (the named pipe), so
// both speak the same wire format via the pure `frame`/`parseContentLength`
// helpers in Lsp.fs. This is the only place the byte-level read/write loop lives.

/// Read one `Content-Length`-framed message. None on stream end or cancellation.
let readMessage (stream: Stream) (ct: CancellationToken) : Async<string option> =
    async {
        let headerBytes = ResizeArray<byte>(128)
        let one = Array.zeroCreate<byte> 1
        let mutable consecutive = 0
        let mutable ended = false
        let mutable haveHeader = false

        while not haveHeader && not ended do
            let! n = stream.ReadAsync(one, 0, 1, ct) |> Async.AwaitTask

            if n = 0 then
                ended <- true
            else
                let b = one[0]
                headerBytes.Add b

                if b = byte '\r' || b = byte '\n' then
                    consecutive <- consecutive + 1
                    if consecutive = 4 then haveHeader <- true
                else
                    consecutive <- 0

        if ended then
            return None
        else
            match parseContentLength (Encoding.ASCII.GetString(headerBytes.ToArray())) with
            | None -> return None
            | Some len ->
                let body = Array.zeroCreate<byte> len
                let mutable read = 0
                let mutable broke = false

                while read < len && not broke do
                    let! n = stream.ReadAsync(body, read, len - read, ct) |> Async.AwaitTask
                    if n = 0 then broke <- true else read <- read + n

                if read < len then return None else return Some(Encoding.UTF8.GetString body)
    }

/// Write one `Content-Length`-framed message and flush.
let writeMessage (stream: Stream) (payload: string) (ct: CancellationToken) : Async<unit> =
    async {
        let bytes = frame payload
        do! stream.WriteAsync(bytes, 0, bytes.Length, ct) |> Async.AwaitTask
        do! stream.FlushAsync ct |> Async.AwaitTask
    }
