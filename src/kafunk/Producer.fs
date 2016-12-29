namespace Kafunk

open FSharp.Control
open Kafunk
open System
open System.Text

/// A producer message.
type ProducerMessage =
  struct
    /// The message payload.
    val value : Binary.Segment
    /// The optional message key.
    val key : Binary.Segment
    new (value:Binary.Segment, key:Binary.Segment) = 
      { value = value ; key = key }
  end
    with

      /// Creates a producer message.
      static member ofBytes (value:Binary.Segment, ?key) =
        ProducerMessage(value, defaultArg key Binary.empty)

      /// Creates a producer message.
      static member ofBytes (value:byte[], ?key) =
        let keyBuf = defaultArg (key |> Option.map Binary.ofArray) Binary.empty
        ProducerMessage(Binary.ofArray value, keyBuf)

      /// Creates a producer message.
      static member ofString (value:string, ?key:string) =
        let keyBuf = defaultArg (key |> Option.map (Encoding.UTF8.GetBytes >> Binary.ofArray)) Binary.empty
        ProducerMessage(Binary.ofArray (Encoding.UTF8.GetBytes value), keyBuf)

/// A producer response.
type ProducerResult =
  struct
    val offsets : (Partition * Offset)[]
    new (os) = { offsets = os }
  end

/// A partition function.
type Partitioner = TopicName * Partition[] * ProducerMessage -> Partition

/// Partition functions.
[<Compile(Module)>]
module Partitioner =

  /// Constantly returns the same partition.
  let konst (p:Partition) : Partitioner =
    konst p

  /// Round-robins across partitions.
  let roundRobin : Partitioner =
    let i = ref 0
    fun (_,ps,_) -> ps.[System.Threading.Interlocked.Increment i % ps.Length]

  /// Computes the hash-code of the routing key to get the topic partition.
  let hashKey (h:Binary.Segment -> int) : Partitioner =
    fun (_,ps,pm) -> ps.[(h pm.key) % ps.Length]


/// Producer state.
type private ProducerState = {
  partitions : Partition[]
  version : int
}

/// Producer configuration.
type ProducerConfig = {

  /// The topic to produce to.
  topic : TopicName

  /// The acks required.
  requiredAcks : RequiredAcks

  /// The compression method to use.
  compression : byte

  /// The maximum time to wait for acknowledgement.
  timeout : Timeout

  /// A partition function which given a topic name, cluster topic metadata and the message payload, returns the partition
  /// which the message should be written to.
  partitioner : Partitioner

  /// When specified, buffers requests by the specified buffer size and buffer timeout to take advantage of batching.
  bufferCountAndTime : (int * int) option

} with

  /// Creates a producer configuration.
  static member create (topic:TopicName, partition:Partitioner, ?requiredAcks:RequiredAcks, ?compression:byte, ?timeout:Timeout, ?bufferSize:int, ?bufferTimeoutMs:int) =
    {
      topic = topic
      requiredAcks = defaultArg requiredAcks RequiredAcks.Local
      compression = defaultArg compression CompressionCodec.None
      timeout = defaultArg timeout 0
      partitioner = partition
      bufferCountAndTime = 
        match bufferSize, bufferTimeoutMs with
        | Some x, Some y -> Some (x,y)
        | _ -> None
    }


/// A producer sends batches of topic and message set pairs to the appropriate Kafka brokers.
type Producer = private {
  conn : KafkaConn
  config : ProducerConfig
  state : MVar<ProducerState>
}

/// High-level producer API.
[<Compile(Module)>]
module Producer =

  open System.Threading
  open System.Threading.Tasks

  let private Log = Log.create "Kafunk.Producer"

  let private getState (conn:KafkaConn) (t:TopicName) (oldVersion:int) = async {
    try
      Log.info "fetching_topic_metadata|topic=%s producer_version=%i" t oldVersion
      let! topicPartitions = conn.GetMetadata [| t |]
      // TODO: handle missing topic errors
      let topicPartitions = topicPartitions |> Map.find t
      return { partitions = topicPartitions ; version = oldVersion + 1 }
    with ex ->
      Log.error "error|%O" ex
      return raise ex }

  /// Resets producer state if caller state has matching version,
  /// otherwise returns the newer version of producer state.
  let private reset (p:Producer) (callerState:ProducerState) =
    p.state
    |> MVar.updateAsync (fun (currentState:ProducerState) -> async {
      if callerState.version = currentState.version then
        return! getState p.conn p.config.topic currentState.version 
      else
        return currentState })

  /// Creates a producer.
  let createAsync (conn:KafkaConn) (cfg:ProducerConfig) : Async<Producer> = async {
    Log.info "initializing_producer|topic=%s" cfg.topic
    let p = { state = MVar.create () ; config = cfg ; conn = conn }
    let init () =
      p.state |> MVar.putAsync (getState conn cfg.topic 0)
    let! state = init ()
    Log.info "producer_initialized|topic=%s partitions=%A" cfg.topic state.partitions
    return p }

  /// Creates a producer.
  let create (conn:KafkaConn) (cfg:ProducerConfig) : Producer =
    createAsync conn cfg |> Async.RunSynchronously

  /// Produces a batch of messages.
  /// Messages are routed based on the configured routing function and
  /// metadata retrieved by the producer.
  let produce (p:Producer) (ms:ProducerMessage[]) = async {

    let conn = p.conn
    let cfg = p.config
    let send = Kafka.produce conn

    let sendBatch (partitions:Partition[]) (ms:ProducerMessage[]) =
      let pms =
        ms
        |> Seq.groupBy (fun pm -> cfg.partitioner (cfg.topic, partitions, pm))
        |> Seq.map (fun (p,pms) ->
          let messages = pms |> Seq.map (fun pm -> Message.create pm.value (Some pm.key) None) 
          //let ms = Compression.compress cfg.compression messages
          let ms = MessageSet.ofMessages messages
          p,ms)
        |> Seq.toArray
      let req = ProduceRequest.ofMessageSetTopics [| cfg.topic, pms |] cfg.requiredAcks cfg.timeout
      send req

    let rec produce (state:ProducerState) (ms:ProducerMessage[]) = async {
      let! res = sendBatch state.partitions ms
      if res.topics.Length = 0 then
        // TODO: handle errors here rather than inside of connection
        let! state' = reset p state
        return! produce state' ms
      else 
        let os = 
          res.topics
          |> Seq.collect (fun (_t,os) ->
            os |> Seq.map (fun (p,_,o) -> p,o))
          |> Seq.toArray
        let res' = ProducerResult(os)
        return res' }

    let! state = MVar.get p.state
    return! produce state ms }