﻿#if INTERACTIVE
#I "../../bin/"
#load "Deedle.fsx"
#r "../../packages/NUnit.2.6.3/lib/nunit.framework.dll"
#r "../../packages/FsCheck.0.9.1.0/lib/net40-Client/FsCheck.dll"
#load "../Common/FsUnit.fs"
#else
module Deedle.Tests.VirtualFrame
#endif

open System
open FsUnit
open NUnit.Framework
open Deedle
open Deedle.Virtual

// ------------------------------------------------------------------------------------------------
// Tracking source
// ------------------------------------------------------------------------------------------------

type LinearSubRange =
  { Offset : int; Step : int }
  interface Vectors.IVectorRange with
    member x.Count = failwith "Count not supported"
  interface seq<int64> with
    member x.GetEnumerator() : System.Collections.Generic.IEnumerator<int64> = failwith "hard!"
  interface System.Collections.IEnumerable with
    member x.GetEnumerator() : System.Collections.IEnumerator = failwith "hard!"

type TrackingSource<'T>(lo, hi, valueAt:int64 -> 'T, ?asLong:'T -> int64, ?search) = 
  member val AccessListCell = ref [] with get, set
  member val LookupListCell = ref [] with get, set
  member val IsTracking = true with get, set
  member val HasMissing = true with get, set
  member x.AccessList = List.rev x.AccessListCell.Value
  member x.LookupList = List.rev x.LookupListCell.Value

  interface IVirtualVectorSource with
    member x.Length = hi - lo + 1L
    member x.ElementType = typeof<'T>

  interface IVirtualVectorSource<'T> with
    member x.LookupRange(v) = 
      match search with
      | Some f -> let o, s = f v in Vectors.Custom { Offset = o; Step = s }
      | None -> failwith "Search not supported"

    member x.LookupValue(k, l, c) = 
      if x.IsTracking then x.LookupListCell := (k, l) :: !x.LookupListCell
      let asLong = match asLong with None -> failwith "Lookup not supported" | Some g -> g
      IndexUtilsModule.binarySearch (hi - lo + 1L) (Func<_, _>(fun i -> asLong (valueAt (lo + i)))) (asLong k) l c
      |> OptionalValue.map (fun i -> valueAt i, i )

    member x.ValueAt addr = 
      //printfn "Value at: %A is %A" addr (lo + addr)
      if x.IsTracking then x.AccessListCell := (lo + addr) :: !x.AccessListCell
      if x.HasMissing && (addr % 3L = 0L) then OptionalValue.Missing
      else OptionalValue(valueAt (lo + addr))

    member x.GetSubVector(range) = 
      match range with
      | Vectors.Range(nlo, nhi) ->
          if nhi < nlo then invalidOp "hi < lo"
          elif nlo < 0L then invalidOp "lo < 0"
          elif nhi > hi then invalidOp "hi > max"
          else TrackingSource
                ( lo+nlo, lo+nhi, valueAt, ?asLong=asLong, HasMissing = x.HasMissing, IsTracking = x.IsTracking, 
                  LookupListCell = x.LookupListCell, AccessListCell = x.AccessListCell ) :> _
      | Vectors.Custom (:? LinearSubRange as lr) ->
          let valueAt i = valueAt(lo + int64 lr.Offset + (int64 lr.Step * i))
          let count = (hi + lo + 1L) / int64 lr.Step 
          let count = if (hi + lo + 1L) % int64 lr.Step > int64 lr.Offset then count+1L else count
          TrackingSource
            ( 0L, count-1L, valueAt, ?asLong=asLong, HasMissing = x.HasMissing, IsTracking = x.IsTracking, 
              LookupListCell = x.LookupListCell, AccessListCell = x.AccessListCell ) :> _
      | _ -> failwith "unexpected custom range!"

type TrackingSource =
  static member CreateLongs(lo, hi) = TrackingSource<int64>(lo, hi, id, id)
  static member CreateFloats(lo, hi) = TrackingSource<float>(lo, hi, float)
  static member CreateStrings(lo, hi) = 
    let strings = "lorem ipsum dolor sit amet consectetur adipiscing elit".Split(' ')
    let search = function
      | LookupKind.Lookup v -> strings |> Seq.findIndex ((=) v), strings.Length
      | _ -> failwith "Scan not supported"
    TrackingSource<string>(lo, hi, (fun i -> strings.[int i % strings.Length]), search=search)
  static member CreateTicks(lo, hi) = 
    let start = DateTimeOffset(DateTime(2000, 1, 1), TimeSpan.FromHours(-1.0))
    let asTicks ticks = start.Ticks + ticks * 987654321L
    TrackingSource<int64>(lo, hi, asTicks, id, HasMissing=false)
  static member CreateTimes(lo, hi) = 
    let start = DateTimeOffset(DateTime(2000, 1, 1), TimeSpan.FromHours(-1.0))
    let asDto ticks = start.AddTicks(ticks * 123456789L)
    TrackingSource<DateTimeOffset>(lo, hi, asDto, (fun dto -> dto.UtcTicks), HasMissing=false)

// ------------------------------------------------------------------------------------------------
// Virtual series tests
// ------------------------------------------------------------------------------------------------

[<Test>]
let ``Formatting accesses only printed values`` () =
  let src = TrackingSource.CreateLongs(0L, 1000000000L)
  let series = Virtual.CreateOrdinalSeries(src)
  series.Format(3, 3) |> ignore
  src.AccessList |> shouldEqual [ 0L; 1L; 2L; 1000000000L-2L; 1000000000L-1L; 1000000000L ]

[<Test>]
let ``Counting keys does not evaluate the series`` () =
  let src = TrackingSource.CreateLongs(0L, 1000000000L)
  let series = Virtual.CreateOrdinalSeries(src)
  series.KeyCount |> shouldEqual 1000000001
  src.AccessList |> shouldEqual []

[<Test>]
let ``Counting values does not evaluate the series`` () =
  let src = TrackingSource.CreateLongs(0L, 10000000L, IsTracking=false)
  let series = Virtual.CreateOrdinalSeries(src)
  series.ValueCount |> shouldEqual 6666667

[<Test>]
let ``Can take, skip etc. without evaluating the series`` () =
  let src = TrackingSource.CreateFloats(0L, 10000000L)
  let s1 = Virtual.CreateOrdinalSeries(src)
  s1 |> Series.take 10 |> Stats.sum |> shouldEqual 27.0
  src.AccessList |> Seq.length |> shouldEqual 10
  s1 |> Series.skipLast (10000000-9) |> Stats.sum |> shouldEqual 27.0
  src.AccessList |> Seq.length |> shouldEqual 20
  s1 |> Series.skip (10000000-9) |> Stats.sum |> shouldEqual 59999973.0
  src.AccessList |> Seq.length |> shouldEqual 30
  s1 |> Series.takeLast 10 |> Stats.sum |> shouldEqual 59999973.0
  src.AccessList |> Seq.length |> shouldEqual 40

[<Test>]
let ``Can perform slicing without evaluating the series`` () = 
  let src = TrackingSource.CreateFloats(0L, 10000000L)
  let s1 = Virtual.CreateOrdinalSeries(src)
  let s2 = s1.[10000000L-9L ..]
  let s3 = s1.[.. 9L]

  (Stats.sum s2) + (Stats.sum s3) |> shouldEqual 60000000.0
  src.AccessList |> Seq.length |> shouldEqual 20
  src.AccessList |> Seq.sum |> shouldEqual 100000000L

[<Test>]
let ``Can access elements by key-based lookup`` () =
  let src = TrackingSource.CreateFloats(0L, 10000000L)
  let s1 = Virtual.CreateOrdinalSeries(src)
  s1.TryGet(1234567L) |> shouldEqual (OptionalValue 1234567.0)
  s1.TryGet(1234568L) |> shouldEqual (OptionalValue 1234568.0)
  s1.TryGet(1234569L) |> shouldEqual OptionalValue.Missing
  src.AccessList |> shouldEqual [1234567L; 1234568L; 1234569L]

[<Test>]
let ``Can materialize virtual series and access it repeatedly`` () =
  let src = TrackingSource.CreateFloats(0L, 10000000L)
  let sv = Virtual.CreateOrdinalSeries(src)
  let sm = sv.[100L .. 200L].Materialize()
  sm |> Stats.mean |> ignore
  sm |> Stats.sum |> ignore
  src.AccessList |> shouldEqual [ 100L .. 200L ]

// ------------------------------------------------------------------------------------------------
// Virutal series with ordered index
// ------------------------------------------------------------------------------------------------

let createTimeSeries () =
  let idxSrc = TrackingSource.CreateTimes(0L, 10000000L)
  let valSrc = TrackingSource.CreateFloats(0L, 10000000L)
  let sv = Virtual.CreateSeries(idxSrc, valSrc)
  idxSrc, valSrc, sv

let date y m d = DateTimeOffset(DateTime(y, m, d), TimeSpan.FromHours(-1.0))
let ith i = (date 2000 1 1).AddTicks(i * 123456789L)
let fromTicks (t:int64) = DateTimeOffset(t, TimeSpan.FromHours(0.0)).ToOffset(TimeSpan.FromHours(8.0))
let toTicks (dto:DateTimeOffset) = dto.UtcTicks

[<Test>]
let ``Can access elements in an ordered time series without evaluating it`` () =
  let isrc, vsrc, s = createTimeSeries()
  s.[ith 5000000L] |> shouldEqual 5000000.0
  s.TryGet(ith 5000001L) |> shouldEqual OptionalValue.Missing
  isrc.LookupList |> shouldEqual [ith 5000000L, Lookup.Exact; ith 5000001L, Lookup.Exact]
  isrc.AccessList |> shouldEqual []
  vsrc.LookupList |> shouldEqual []
  vsrc.AccessList |> shouldEqual [5000000L; 5000001L]

[<Test>]
let ``Can use different lookup behaviours when accessing time series values`` () = 
  let isrc, vsrc, s = createTimeSeries()
  s.Get(ith 5000001L, Lookup.ExactOrGreater) |> shouldEqual 5000002.0
  s.Get(ith 5000001L, Lookup.ExactOrSmaller) |> shouldEqual 5000000.0
  s.Get(ith 5000000L, Lookup.Greater) |> shouldEqual 5000002.0
  s.Get(ith 5000000L, Lookup.Smaller) |> shouldEqual 4999999.0
  isrc.LookupList |> Seq.length |> shouldEqual 4
  isrc.AccessList |> shouldEqual []
  set vsrc.AccessList |> shouldEqual <| set [ 4999999L .. 5000002L ]

[<Test>]
let ``Can perform slicing on time series without evaluating it`` () =
  let isrc, vsrc, s1 = createTimeSeries()
  
  // TODO: s1.[x] = s2.[x]
  // s1.[ith 2778364L]

  let s2 = s1.[date 2001 1 1 .. date 2001 2 1]
  // s2.[ith 2778364L]
  fst s2.KeyRange |> should be (greaterThanOrEqualTo (date 2001 1 1))
  snd s2.KeyRange |> should be (lessThanOrEqualTo (date 2001 2 1))
  s2.[ith 2700001L] |> shouldEqual <| s1.[ith 2700001L]
  isrc.AccessList |> set |> Seq.length |> shouldEqual 2
  isrc.LookupList |> set |> Seq.length |> shouldEqual 3
  vsrc.AccessList |> set |> Seq.length |> shouldEqual 1

// ------------------------------------------------------------------------------------------------
// Virtual frame tests
// ------------------------------------------------------------------------------------------------

let createSimpleFrameSize size =
  let s1 = TrackingSource.CreateLongs(0L, size)
  let s2 = TrackingSource.CreateStrings(0L, size)
  let frame = Virtual.CreateOrdinalFrame(["S1"; "S2"], [s1; s2])
  s1, s2, frame

let createSimpleFrame() = createSimpleFrameSize(10000000L)

let createSimpleTimeFrame() =
  let idxSrc = TrackingSource.CreateTimes(0L, 10000000L)
  let s1 = TrackingSource.CreateLongs(0L, 10000000L)
  let s2 = TrackingSource.CreateStrings(0L, 10000000L, HasMissing=false)
  let frame = Virtual.CreateFrame(idxSrc, ["S1"; "S2"], [s1; s2] )
  idxSrc, s1, s2, frame

let createNumericFrame() =
  let s1 = TrackingSource.CreateFloats(0L, 10000000L, HasMissing=false)
  let s2 = TrackingSource.CreateFloats(0L, 10000000L)
  let frame = Virtual.CreateOrdinalFrame( ["Dense"; "Sparse"], [s1; s2] )
  s1, s2, frame

let createTicksFrame() =
  let s1 = TrackingSource.CreateTicks(0L, 10000000L)
  let s2 = TrackingSource.CreateFloats(0L, 10000000L)
  let frame = Virtual.CreateOrdinalFrame( ["Ticks"; "Values"], [s1; s2] )
  s1, s2, frame

// ------------------------------------------------------------------------------------------------

[<Test>]
let ``Can format virtual frame without evaluating it`` () = 
  let s1, s2, frame = createSimpleFrame()
  frame.Format(2, 2) |> ignore
  s1.AccessList |> shouldEqual [0L; 1L; 9999999L; 10000000L]
  s2.AccessList |> shouldEqual [0L; 1L; 9999999L; 10000000L]

[<Test>]
let ``Accessing row evaluates only the required values`` () = 
  let s1, s2, frame = createSimpleFrame()
  frame.GetRow<obj>(5000000L).["S1"] |> shouldEqual <| box 5000000L
  frame.["S2", 5000000L] |> shouldEqual <| box "lorem"
  s1.AccessList |> shouldEqual [5000000L]
  s2.AccessList |> shouldEqual [5000000L]

[<Test>]
let ``Accessing series of rows accesses only required values`` () =
  let s1, s2, frame = createSimpleFrame()
  frame.Rows.Format(2,2) |> ignore
  s1.AccessList |> shouldEqual [0L; 1L; 9999999L; 10000000L]
  s2.AccessList |> shouldEqual [0L; 1L; 9999999L; 10000000L]

[<Test>]
let ``Can use ColumnsApply and 'sin' witout evaluating a frame`` () =
  let s1 = TrackingSource.CreateFloats(0L, 10000000000L)
  let s2 = TrackingSource.CreateFloats(0L, 10000000000L)
  let f1 = Virtual.CreateOrdinalFrame( ["S1"; "S2"], [s1; s2] )
  let f2 = f1.ColumnApply<float>(fun s -> s |> Series.mapValues (fun v -> v / 1000000000.0) :> _)
  let f3 = sin f2
  f3.GetRow<float>(3141592654L) |> Stats.mean |> should (equalWithin 1.0e-8) 0.0
  s1.AccessList |> shouldEqual [3141592654L]
  s2.AccessList |> shouldEqual [3141592654L]

[<Test>]
let ``Can map over frame rows without evaluating it`` () = 
  let s1, s2, frame = createSimpleFrame()
  let mapped = frame |> Frame.mapRows (fun k row -> sqrt row?S1)
  mapped.[10000L] |> shouldEqual 100.0
  s1.AccessList |> shouldEqual [10000L]
  s2.AccessList |> shouldEqual [10000L] // TODO: Improve implementation of CombineN, so that this is empty!

[<Test>]
let ``Can perform slicing on frame using the Rows property`` () =
  let s1, s2, f1 = createSimpleFrame()
  let f2 = f1.Rows.[100L .. 999900L]
  let f3 = f2.Rows.[1000L .. 999000L]
  let f4 = f3.Rows.[500000L .. 500005L]

  f4.RowIndex.KeyRange
  |> shouldEqual (500000L, 500005L)

  f4.GetColumn<string>("S2") 
  |> Series.values
  |> List.ofSeq
  |> shouldEqual ["ipsum"; "dolor"; "amet"; "consectetur"]

[<Test>]
let ``Can access Columns of a virtual frame without evaluating the data`` () =
  let s1, s2, f = createSimpleFrame()
  let cols = f.Columns
  cols.Keys |> List.ofSeq |> shouldEqual ["S1"; "S2"]
  cols.["S1"].[10L] |> unbox |> shouldEqual 10L
  s1.AccessList |> shouldEqual [10L]
  s2.AccessList |> shouldEqual []

[<Test>]
let ``Can add computed series as a new column to a frame with the same index``() = 
  let s1, s2, f = createNumericFrame()
  let times = f |> Frame.mapRows (fun _ row -> 
    let t = row.GetAs<int64>("Dense")
    DateTimeOffset(DateTime(2000,1,1).AddTicks(t * 1233456789L), TimeSpan.FromHours(1.0)) )
  f.AddColumn("Times", times)
  f.GetRow<obj>(5000001L).["Dense"] |> shouldEqual (box 5000001L)
  f.GetRow<obj>(5000001L).TryGet("Sparse") |> shouldEqual OptionalValue.Missing
  (f.GetRow<obj>(5000001L).["Times"] |> unbox<DateTimeOffset>).Year |> shouldEqual 2019
  set s1.AccessList |> shouldEqual <| set [5000001L]
  set s2.AccessList |> shouldEqual <| set [5000001L]

[<Test>]
let ``Can index frame by a ordered column computed using series transform`` () =
  let s1, s2, f = createTicksFrame()
  f?Times <- f.GetColumn<int64>("Ticks") |> Series.convert fromTicks toTicks
  let byTimes = f |> Frame.indexRowsDateOffs "Times"

  byTimes.Rows.TryGet(date 2010 1 1, Lookup.Exact) |> shouldEqual OptionalValue.Missing
  let prev = byTimes.Rows.Get(date 2010 1 1, Lookup.ExactOrSmaller).["Ticks"] |> unbox<int64> |> fromTicks
  let next = byTimes.Rows.Get(date 2010 1 1, Lookup.ExactOrGreater).["Ticks"] |> unbox<int64> |> fromTicks
  prev < date 2010 1 1 |> shouldEqual true
  next > date 2010 1 1 |> shouldEqual true
  ((date 2010 1 1) - prev).Ticks + (next - (date 2010 1 1)).Ticks |> shouldEqual 987654321L

[<Test>]
let ``Can filter virtual frame by a value in a non-index column`` () = 
  let idx, s1, s2, f = createSimpleTimeFrame()
  let partsLength =
    "lorem ipsum dolor sit amet consectetur adipiscing elit".Split(' ')
    |> Seq.map (fun s -> f |> Frame.filterRowsBy "S2" s)
    |> Seq.map (fun f -> f.RowCount)
    |> Seq.sum
  partsLength |> shouldEqual f.RowCount
  idx.AccessList |> shouldEqual []
  s1.AccessList |> shouldEqual []
  s2.AccessList |> shouldEqual []

[<Test>]
let ``Can access items of a virtual filtered frame without evaluating it`` () =
  let idx, s1, s2, f = createSimpleTimeFrame()
  let lorem = f |> Frame.filterRowsBy "S2" "lorem"
  lorem.Rows.[ith 5000000L].["S2"] |> unbox |> shouldEqual "lorem"
  lorem.Rows.TryGet(ith 5000001L) |> shouldEqual OptionalValue.Missing
  lorem.Rows.Get(date 2001 1 1, Lookup.ExactOrSmaller).["S2"] |> unbox |> shouldEqual "lorem"
  lorem.Rows.Get(date 2001 1 1, Lookup.ExactOrGreater).["S2"] |> unbox |> shouldEqual "lorem"  
  lorem.Rows.Get(date 2001 1 1, Lookup.ExactOrGreater)?S1 - lorem.Rows.Get(date 2001 1 1, Lookup.ExactOrSmaller)?S1 |> shouldEqual 8.0
  set s1.AccessList |> shouldEqual <| set [320176L; 320177L; 625000L]
  set s2.AccessList |> shouldEqual <| set [320176L; 320177L; 625000L]

[<Test>]
let ``Filtering items by value behaves correctly at the beginning & end`` () = 
  let idx, s1, s2, f = createSimpleTimeFrame()
  let lastValues = 
    "lorem ipsum dolor sit amet consectetur adipiscing elit".Split(' ')
    |> Seq.map (fun s -> f |> Frame.filterRowsBy "S2" s)
    |> Seq.map (fun f -> int (f?S1.GetAt(int (f.RowIndex.KeyCount - 1L))))
    |> set
  lastValues |> shouldEqual <| set [9999993; 9999994; 9999995; 9999996; 9999997; 9999998; 9999999; 10000000]
 

//let idx, s1, s2, f = createSimpleTimeFrame() // TODO: THe reindexing only works with RAW frames atm.


// f.Rows
// |> Series.filter (fun _ row -> row.GetAs<string>("S2").Length > 5)

//let s1, s2, f = createSimpleFrameSize(100000L)
//let cond = f.Rows |> Series.map (fun _ row -> row.GetAs<string>("S2").Length > 5)
//f?Cond <- cond
//f |> Frame.filterRowsBy "Cond" true





// TODO: Tests for frame with datetimeoffset index

// TODO: Filtering ???
// TODO: Append/merge frames

// TODO: ColumnApply does not work when the frame contains non-numerical columns
// ...because we delay things, it delays the attempt to convert string -> float :-(
// We should be able to check the type of the column (at least)

// TODO: What if we need to build index from two columns, say 'utcTicks' and 'offset' ??
// This is not reversible: f |> Frame.mapRows (fun _ row -> niceTimeFromTicks (row.GetAs "Dense"))

