﻿namespace GWallet.Backend

open System
open System.Linq
open System.Threading.Tasks

type ServerUnavailabilityException (message:string, lastException: Exception) =
    inherit Exception (message, lastException)

type private NoneAvailableException (message:string, lastException: Exception) =
   inherit ServerUnavailabilityException (message, lastException)

type private NotEnoughAvailableException (message:string, lastException: Exception) =
   inherit ServerUnavailabilityException (message, lastException)

type ResultInconsistencyException (totalNumberOfSuccesfulResultsObtained: int,
                                   maxNumberOfConsistentResultsObtained: int,
                                   numberOfConsistentResultsRequired: uint16) =
  inherit Exception ("Results obtained were not enough to be considered consistent" +
                      sprintf " (received: %d, consistent: %d, required: %d)"
                                  totalNumberOfSuccesfulResultsObtained
                                  maxNumberOfConsistentResultsObtained
                                  numberOfConsistentResultsRequired)

type internal ResultsSoFar<'R> = List<'R>
type internal ExceptionsSoFar<'T,'R,'E when 'E :> Exception> = List<('T->'R)*'E>
type internal FinalResult<'T,'R,'E when 'E :> Exception> =
    | ConsistentResult of 'R
    | AverageResult of 'R
    | InconsistentOrNotEnoughResults of ResultsSoFar<'R>*ExceptionsSoFar<'T,'R,'E>

type internal NonParallelResultWithAdditionalWork<'T,'R,'E when 'E :> Exception> =
    | SuccessfulFirstResult of ('R * Async<NonParallelResults<'T,'R,'E>>)
    | NoneAvailable
and internal NonParallelResults<'T,'R,'E when 'E :> Exception> =
    ExceptionsSoFar<'T,'R,'E> * NonParallelResultWithAdditionalWork<'T,'R,'E>

type ConsistencySettings<'R> =
    | NumberOfConsistentResponsesRequired of uint16
    | AverageBetweenResponses of (uint16 * (list<'R> -> 'R))

type FaultTolerantParallelClientSettings<'R> =
    {
        NumberOfMaximumParallelJobs: uint16;
        ConsistencyConfig: ConsistencySettings<'R>;
        NumberOfRetries: uint16;
        NumberOfRetriesForInconsistency: uint16;
    }

type FaultTolerantParallelClient<'E when 'E :> Exception>() =
    do
        if typeof<'E> = typeof<Exception> then
            raise (ArgumentException("'E cannot be System.Exception, use a derived one", "'E"))

    let MeasureConsistency (results: List<'R>) =
        results |> Seq.countBy id |> Seq.sortByDescending (fun (_,count: int) -> count) |> List.ofSeq

    let LaunchAsyncJobs(jobs:List<Async<NonParallelResults<'T,'R,'E>>>): List<Task<NonParallelResults<'T,'R,'E>>> =
        jobs |> Seq.map (fun asyncJob -> Async.StartAsTask asyncJob) |> List.ofSeq

    let rec WhenSomeInternal (consistencySettings: ConsistencySettings<'R>)
                             (tasks: List<Task<NonParallelResults<'T,'R,'E>>>)
                             (resultsSoFar: List<'R>)
                             (failedFuncsSoFar: ExceptionsSoFar<'T,'R,'E>)
                             : Async<FinalResult<'T,'R,'E>> = async {
        match tasks with
        | [] ->
            return InconsistentOrNotEnoughResults(resultsSoFar,failedFuncsSoFar)
        | theTasks ->

            let taskToWaitForFirstFinishedTask = Task.WhenAny theTasks
            let! fastestTask = Async.AwaitTask taskToWaitForFirstFinishedTask
            let failuresOfTask,resultOfTask = fastestTask.Result

            let restOfTasks: List<Task<NonParallelResults<'T,'R,'E>>> =
                theTasks.Where(fun task -> not (Object.ReferenceEquals(task, fastestTask))) |> List.ofSeq

            let (newResults,newRestOfTasks) =
                match resultOfTask with
                | SuccessfulFirstResult(newResult,unlaunchedJobWithMoreTasks) ->
                    let newTask = Async.StartAsTask unlaunchedJobWithMoreTasks
                    (newResult::resultsSoFar),(newTask::restOfTasks)
                | NoneAvailable ->
                    resultsSoFar,restOfTasks
            let newFailedFuncs = List.append failedFuncsSoFar failuresOfTask

            match consistencySettings with
            | AverageBetweenResponses (minimumNumberOfResponses,averageFunc) ->
                if (newResults.Length >= int minimumNumberOfResponses) then
                    return AverageResult (averageFunc newResults)
                else
                    return! WhenSomeInternal consistencySettings newRestOfTasks newResults newFailedFuncs
            | NumberOfConsistentResponsesRequired number ->
                let resultsSortedByCount = MeasureConsistency newResults
                match resultsSortedByCount with
                | [] ->
                    return! WhenSomeInternal consistencySettings newRestOfTasks newResults newFailedFuncs
                | (mostConsistentResult,maxNumberOfConsistentResultsObtained)::_ ->
                    if (maxNumberOfConsistentResultsObtained = int number) then
                        return ConsistentResult mostConsistentResult
                    else
                        return! WhenSomeInternal consistencySettings newRestOfTasks newResults newFailedFuncs
    }

    // at the time of writing this, I only found a Task.WhenAny() equivalent function in the asyncF# world, called
    // "Async.WhenAny" in TomasP's tryJoinads source code, however it seemed a bit complex for me to wrap my head around
    // it (and I couldn't just consume it and call it a day, I had to modify it to be "WhenSome" instead of "WhenAny",
    // as in when N>1), so I decided to write my own, using Tasks to make sure I would not spawn duplicate jobs
    let WhenSome (consistencySettings: ConsistencySettings<'R>)
                 (jobs: List<Async<NonParallelResults<'T,'R,'E>>>)
                 (resultsSoFar: List<'R>)
                 (failedFuncsSoFar: ExceptionsSoFar<'T,'R,'E>)
                 : Async<FinalResult<'T,'R,'E>> =
        let tasks = LaunchAsyncJobs jobs
        WhenSomeInternal consistencySettings tasks resultsSoFar failedFuncsSoFar

    let rec ConcatenateNonParallelFuncs (args: 'T) (failuresSoFar: ExceptionsSoFar<'T,'R,'E>) (funcs: List<'T->'R>)
                                        : Async<NonParallelResults<'T,'R,'E>> =
        match funcs with
        | [] ->
            async {
                return failuresSoFar,NoneAvailable
            }
        | head::tail ->
            async {
                try
                    let result = head args
                    let tailAsync = ConcatenateNonParallelFuncs args failuresSoFar tail
                    return failuresSoFar,SuccessfulFirstResult(result,tailAsync)
                with
                | :? 'E as ex ->
                    if (Config.DebugLog) then
                        Console.Error.WriteLine (sprintf "Fault warning: %s: %s"
                                                     (ex.GetType().FullName)
                                                     ex.Message)
                    let newFailures = (head,ex)::failuresSoFar
                    return! ConcatenateNonParallelFuncs args newFailures tail
            }

    let rec QueryInternal (settings: FaultTolerantParallelClientSettings<'R>)
                          (args: 'T)
                          (funcs: List<'T->'R>)
                          (resultsSoFar: List<'R>)
                          (failedFuncsSoFar: ExceptionsSoFar<'T,'R,'E>)
                          (retries: uint16)
                          (retriesForInconsistency: uint16)
                              : Async<'R> = async {
        if not (funcs.Any()) then
            return raise(ArgumentException("number of funcs must be higher than zero",
                                           "funcs"))
        let howManyFuncs = uint16 funcs.Length
        let numberOfMaximumParallelJobs = int settings.NumberOfMaximumParallelJobs

        match settings.ConsistencyConfig with
        | NumberOfConsistentResponsesRequired numberOfConsistentResponsesRequired ->
            if numberOfConsistentResponsesRequired < uint16 1 then
                raise (ArgumentException("must be higher than zero", "numberOfConsistentResponsesRequired"))
            if (howManyFuncs < numberOfConsistentResponsesRequired) then
                return raise(ArgumentException("number of funcs must be equal or higher than numberOfConsistentResponsesRequired",
                                               "funcs"))
        | AverageBetweenResponses(minimumNumberOfResponses,averageFunc) ->
            if (int minimumNumberOfResponses > numberOfMaximumParallelJobs) then
                return raise(ArgumentException("numberOfMaximumParallelJobs should be equal or higher than minimumNumberOfResponses for the averageFunc",
                                               "settings"))

        let funcsToRunInParallel,restOfFuncs =
            if (howManyFuncs > settings.NumberOfMaximumParallelJobs) then
                funcs |> Seq.take numberOfMaximumParallelJobs, funcs |> Seq.skip numberOfMaximumParallelJobs
            else
                funcs |> Seq.ofList, Seq.empty

        // each bucket can be run in parallel, each bucket contains 1 or more funcs that cannot be run in parallel
        // e.g. if we have funcs A, B, C, D and numberOfMaximumParallelJobs=2, then we have funcBucket1(A,B) and
        //      funcBucket2(C,D), then fb1&fb2 are started at the same time (A&C start at the same time), and B
        //      starts only when A finishes or fails, and D only starts when C finishes or fails
        let funcBuckets =
            Seq.splitInto numberOfMaximumParallelJobs funcs
            |> Seq.map List.ofArray
            |> Seq.map (ConcatenateNonParallelFuncs args List.empty)
            |> List.ofSeq

        if (funcBuckets.Length <> numberOfMaximumParallelJobs) then
            return failwithf "Assertion failed, splitInto didn't work as expected? got %d, should be %d"
                             funcBuckets.Length numberOfMaximumParallelJobs

        let! result =
            WhenSome settings.ConsistencyConfig funcBuckets resultsSoFar failedFuncsSoFar
        match result with
        | AverageResult averageResult ->
            return averageResult
        | ConsistentResult consistentResult ->
            return consistentResult
        | InconsistentOrNotEnoughResults(allResultsSoFar,failedFuncsWithTheirExceptions) ->
            let failedFuncs: List<'T->'R> = failedFuncsWithTheirExceptions |> List.map fst
            if (allResultsSoFar.Length = 0) then
                if (retries = settings.NumberOfRetries) then
                    let firstEx = failedFuncsWithTheirExceptions.First() |> snd
                    return raise (NoneAvailableException("Not available", firstEx))
                else
                    return! QueryInternal settings
                                          args
                                          failedFuncs
                                          allResultsSoFar
                                          []
                                          (uint16 (retries + uint16 1))
                                          retriesForInconsistency
            else
                let totalNumberOfSuccesfulResultsObtained = allResultsSoFar.Length
                match settings.ConsistencyConfig with
                | NumberOfConsistentResponsesRequired numberOfConsistentResponsesRequired ->
                    let resultsOrderedByCount = MeasureConsistency allResultsSoFar
                    match resultsOrderedByCount with
                    | [] ->
                        return failwith "resultsSoFar.Length != 0 but MeasureConsistency returns None, please report this bug"
                    | (mostConsistentResult,maxNumberOfConsistentResultsObtained)::_ ->
                        if (retriesForInconsistency = settings.NumberOfRetriesForInconsistency) then


                            return raise (ResultInconsistencyException(totalNumberOfSuccesfulResultsObtained,
                                                                       maxNumberOfConsistentResultsObtained,
                                                                       numberOfConsistentResponsesRequired))
                        else
                            return! QueryInternal settings
                                                  args
                                                  funcs
                                                  []
                                                  []
                                                  retries
                                                  (uint16 (retriesForInconsistency + uint16 1))
                | AverageBetweenResponses(minimumNumberOfResponses,averageFunc) ->
                    if (retries = settings.NumberOfRetries) then
                        let firstEx = failedFuncsWithTheirExceptions.First() |> snd
                        return raise (NotEnoughAvailableException("resultsSoFar.Length != 0 but not enough to satisfy minimum number of results for averaging func", firstEx))
                    else
                        return! QueryInternal settings
                                              args
                                              failedFuncs
                                              allResultsSoFar
                                              failedFuncsWithTheirExceptions
                                              (uint16 (retries + uint16 1))
                                              retriesForInconsistency

    }

    member self.Query<'T,'R when 'R : equality> (settings: FaultTolerantParallelClientSettings<'R>)
                                                (args: 'T)
                                                (funcs: list<'T->'R>): Async<'R> =
        if settings.NumberOfMaximumParallelJobs < uint16 1 then
            raise (ArgumentException("must be higher than zero", "numberOfMaximumParallelJobs"))

        QueryInternal settings args funcs [] [] (uint16 0) (uint16 0)
