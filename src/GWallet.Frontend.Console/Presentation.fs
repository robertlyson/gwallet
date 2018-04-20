﻿namespace GWallet.Frontend.Console

open System
open System.Text.RegularExpressions

open GWallet.Backend

type CurrencyType =
    Fiat | Crypto

module Presentation =

    let Error(message: string): unit =
        Console.Error.WriteLine("Error: " + message)

    // FIXME: share code between Frontend.Console and Frontend.XF
    // with this we want to avoid the weird default US format of starting with the month, then day, then year... sigh
    let ShowSaneDate (date: DateTime): string =
        date.ToString("dd-MMM-yyyy")

    // FIXME: share code between Frontend.Console and Frontend.XF
    let ShowDecimalForHumans currencyType (amount: decimal): string =
        let amountOfDecimalsToShow =
            match currencyType with
            | CurrencyType.Fiat -> 2
            | CurrencyType.Crypto -> 5

        Math.Round(amount, amountOfDecimalsToShow)

            // line below is to add thousand separators and not show zeroes on the right...
            .ToString("N" + amountOfDecimalsToShow.ToString())

    let ConvertPascalCaseToSentence(pascalCaseElement: string) =
        Regex.Replace(pascalCaseElement, "[a-z][A-Z]",
                      (fun (m: Match) -> m.Value.[0].ToString() + " " + Char.ToLower(m.Value.[1]).ToString()))

    let internal ExchangeRateUnreachableMsg = " (USD exchange rate unreachable... offline?)"

    let ShowFee (estimatedFee: IBlockchainFeeInfo) =
        let currency = estimatedFee.Currency
        let estimatedFeeInUsd =
            match FiatValueEstimation.UsdValue(currency) with
            | Fresh(usdValue) ->
                sprintf "(~%s USD)"
                    (usdValue * estimatedFee.FeeValue |> ShowDecimalForHumans CurrencyType.Fiat)
            | NotFresh(Cached(usdValue,time)) ->
                sprintf "(~%s USD [last known rate at %s])"
                    (usdValue * estimatedFee.FeeValue |> ShowDecimalForHumans CurrencyType.Fiat)
                    (time |> ShowSaneDate)
            | NotFresh(NotAvailable) -> ExchangeRateUnreachableMsg
        Console.WriteLine(sprintf "Estimated fee for this transaction would be:%s %s %A %s"
                              Environment.NewLine
                              (estimatedFee.FeeValue |> ShowDecimalForHumans CurrencyType.Crypto)
                              currency
                              estimatedFeeInUsd
                         )

    let ShowTransactionData<'T when 'T:> IBlockchainFeeInfo> (trans: UnsignedTransaction<'T>) =
        let maybeUsdPrice = FiatValueEstimation.UsdValue(trans.Proposal.Currency)
        let maybeEstimatedAmountInUsd: Option<string> =
            match maybeUsdPrice with
            | Fresh(usdPrice) ->
                Some(sprintf "~ %s USD"
                             (trans.Proposal.Amount.ValueToSend * usdPrice
                                 |> ShowDecimalForHumans CurrencyType.Fiat))
            | NotFresh(Cached(usdPrice, time)) ->
                Some(sprintf "~ %s USD (last exchange rate known at %s)"
                        (trans.Proposal.Amount.ValueToSend * usdPrice
                            |> ShowDecimalForHumans CurrencyType.Fiat)
                        (time |> ShowSaneDate))
            | NotFresh(NotAvailable) -> None

        Console.WriteLine("Transaction data:")
        Console.WriteLine("Sender: " + trans.Proposal.OriginAddress)
        Console.WriteLine("Recipient: " + trans.Proposal.DestinationAddress)
        let fiatAmount =
            match maybeEstimatedAmountInUsd with
            | Some(estimatedAmountInUsd) -> estimatedAmountInUsd
            | _ -> String.Empty
        Console.WriteLine (sprintf "Amount: %s %A %s"
                                   (trans.Proposal.Amount.ValueToSend |> ShowDecimalForHumans CurrencyType.Crypto)
                                   trans.Proposal.Currency
                                   fiatAmount)
        Console.WriteLine()
        ShowFee trans.Metadata
