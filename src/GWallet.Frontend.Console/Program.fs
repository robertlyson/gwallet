﻿open System
open System.Linq
open System.Text.RegularExpressions

open GWallet.Backend
open GWallet.Frontend.Console

let rec TrySendAmount account transactionMetadata destination amount =
    let password = UserInteraction.AskPassword false
    try
        let txIdUri =
            Account.SendPayment account transactionMetadata destination amount password
                |> Async.RunSynchronously
        Console.WriteLine(sprintf "Transaction successful:%s%s" Environment.NewLine (txIdUri.ToString()))
        UserInteraction.PressAnyKeyToContinue ()
    with
    | :? DestinationEqualToOrigin ->
        Presentation.Error "Transaction's origin cannot be the same as the destination."
        UserInteraction.PressAnyKeyToContinue()
    | :? InsufficientFunds ->
        Presentation.Error "Insufficient funds."
        UserInteraction.PressAnyKeyToContinue()
    | :? InvalidPassword ->
        Presentation.Error "Invalid password, try again."
        TrySendAmount account transactionMetadata destination amount

let rec TrySign account unsignedTrans =
    let password = UserInteraction.AskPassword false
    try
        Account.SignUnsignedTransaction account unsignedTrans password
    with
    // TODO: would this throw insufficient funds? test
    //| :? InsufficientFunds ->
    //    Presentation.Error "Insufficient funds."
    | :? InvalidPassword ->
        Presentation.Error "Invalid password, try again."
        TrySign account unsignedTrans

let BroadcastPayment() =
    let fileToReadFrom = UserInteraction.AskFileNameToLoad
                             "Introduce a file name to load the signed transaction: "
    let signedTransaction = Account.LoadSignedTransactionFromFile fileToReadFrom.FullName
    //TODO: check if nonce matches, if not, reject trans

    // FIXME: we should be able to infer the trans info from the raw transaction! this way would be more secure too
    Presentation.ShowTransactionData(signedTransaction.TransactionInfo)
    if UserInteraction.AskYesNo "Do you accept?" then
        let txIdUri =
            Account.BroadcastTransaction signedTransaction
                |> Async.RunSynchronously
        Console.WriteLine(sprintf "Transaction successful:%s%s" Environment.NewLine (txIdUri.ToString()))
        UserInteraction.PressAnyKeyToContinue ()

let SignOffPayment() =
    let fileToReadFrom = UserInteraction.AskFileNameToLoad
                             "Introduce a file name to load the unsigned transaction: "
    let unsignedTransaction = Account.LoadUnsignedTransactionFromFile fileToReadFrom.FullName

    let accountsWithSameAddress =
        Account.GetAllActiveAccounts().Where(fun acc -> acc.PublicAddress = unsignedTransaction.Proposal.OriginAddress)
    if not (accountsWithSameAddress.Any()) then
        Presentation.Error "The transaction doesn't correspond to any of the accounts in the wallet."
        UserInteraction.PressAnyKeyToContinue ()
    else
        let accounts =
            accountsWithSameAddress.Where(
                fun acc -> acc.Currency = unsignedTransaction.Proposal.Currency &&
                           acc :? NormalAccount)
        if not (accounts.Any()) then
            Presentation.Error(
                sprintf
                    "The transaction corresponds to an address of the accounts in this wallet, but it's a readonly account or it maps a different currency than %A."
                    unsignedTransaction.Proposal.Currency
            )
            UserInteraction.PressAnyKeyToContinue()
        else
            let account = accounts.First()
            if (accounts.Count() > 1) then
                failwith "More than one normal account matching address and currency? Please report this issue."

            match account with
            | :? ReadOnlyAccount as readOnlyAccount ->
                failwith "Previous account filtering should have discarded readonly accounts already. Please report this issue"
            | :? NormalAccount as normalAccount ->
                Console.WriteLine ("Importing external data...")
                Caching.SaveSnapshot unsignedTransaction.Cache

                Console.WriteLine ("Account to use when signing off this transaction:")
                Console.WriteLine ()
                UserInteraction.DisplayAccountStatuses (WhichAccount.MatchingWith(account)) |> ignore
                Console.WriteLine()

                Presentation.ShowTransactionData unsignedTransaction

                if UserInteraction.AskYesNo "Do you accept?" then
                    let trans = TrySign normalAccount unsignedTransaction
                    Console.WriteLine("Transaction signed.")
                    Console.Write("Introduce a file name or path to save it: ")
                    let filePathToSaveTo = Console.ReadLine()
                    Account.SaveSignedTransaction trans filePathToSaveTo
                    Console.WriteLine("Transaction signed and saved successfully. Now copy it to the online device.")
                    UserInteraction.PressAnyKeyToContinue ()
            | _ ->
                failwith "Account type not supported. Please report this issue."

let SendPaymentOfSpecificAmount (account: IAccount)
                                (amount: TransferAmount)
                                (transactionMetadata: IBlockchainFeeInfo)
                                (destination: string) =
    match account with
    | :? ReadOnlyAccount as readOnlyAccount ->
        Console.WriteLine("Cannot send payments from readonly accounts.")
        Console.Write("Introduce a file name to save the unsigned transaction: ")
        let filePath = Console.ReadLine()
        let proposal = {
            Currency = account.Currency;
            OriginAddress = account.PublicAddress;
            Amount = amount;
            DestinationAddress = destination;
        }
        Account.SaveUnsignedTransaction proposal transactionMetadata filePath
        Console.WriteLine("Transaction saved. Now copy it to the device with the private key.")
        UserInteraction.PressAnyKeyToContinue()
    | :? NormalAccount as normalAccount ->
        TrySendAmount normalAccount transactionMetadata destination amount
    | _ ->
        failwith ("Account type not recognized: " + account.GetType().FullName)

let SendPayment() =
    let account = UserInteraction.AskAccount()
    let destination = UserInteraction.AskPublicAddress account.Currency "Destination address: "
    let maybeAmount = UserInteraction.AskAmount account
    match maybeAmount with
    | None -> ()
    | Some(amount) ->
        let maybeFee = UserInteraction.AskFee account amount destination
        match maybeFee with
        | None -> ()
        | Some(fee) ->
            SendPaymentOfSpecificAmount account amount fee destination

let rec TryArchiveAccount account =
    let password = UserInteraction.AskPassword(false)
    try
        Account.Archive account password
        Console.WriteLine "Account archived."
        UserInteraction.PressAnyKeyToContinue ()
    with
    | :? InvalidPassword ->
        Presentation.Error "Invalid password, try again."
        TryArchiveAccount account

let rec AddReadOnlyAccount() =
    let currencyChosen = UserInteraction.AskCurrency false
    match currencyChosen with
    | None -> failwith "Currency should not return None if allowAll=false"
    | Some(currency) ->
        let publicAddress = UserInteraction.AskPublicAddress currency "Public address: "
        try
            Account.AddPublicWatcher currency publicAddress
        with
        | :? AccountAlreadyAdded ->
            Console.Error.WriteLine("Account had already been added")

let ArchiveAccount() =
    let account = UserInteraction.AskAccount()
    match account with
    | :? ReadOnlyAccount as readOnlyAccount ->
        Console.WriteLine("Read-only accounts cannot be archived, but just removed entirely.")
        if not (UserInteraction.AskYesNo "Do you accept?") then
            ()
        else
            Account.RemovePublicWatcher readOnlyAccount
            Console.WriteLine "Read-only account removed."
            UserInteraction.PressAnyKeyToContinue()
    | :? NormalAccount as normalAccount ->
        let balance = Account.GetShowableBalance account |> Async.RunSynchronously
        match balance with
        | NotFresh(NotAvailable) ->
            Presentation.Error "Removing accounts when offline is not supported."
            ()
        | Fresh(amount) | NotFresh(Cached(amount,_)) ->
            if (amount > 0m) then
                Presentation.Error "Please empty the account before removing it."
                UserInteraction.PressAnyKeyToContinue ()
            else
                Console.WriteLine ()
                Console.WriteLine "Please note: "
                Console.WriteLine "Just in case this account receives funds in the future by mistake, "
                Console.WriteLine "the operation of archiving an account doesn't entirely remove it."
                Console.WriteLine ()
                Console.WriteLine "You will be asked the password of it now so that its private key can remain unencrypted in the configuration folder, in order for you to be able to safely forget this password."
                Console.WriteLine "Then this account will be watched constantly and if new payments are detected, "
                Console.WriteLine "GWallet will prompt you to move them to a current account without the need of typing the old password."
                Console.WriteLine ()
                if not (UserInteraction.AskYesNo "Do you accept?") then
                    ()
                else
                    TryArchiveAccount normalAccount
    | _ ->
        failwith (sprintf "Account type not valid for archiving: %s. Please report this issue."
                      (account.GetType().FullName))

let rec PerformOptions(numAccounts: int) =
    match UserInteraction.AskOption(numAccounts) with
    | Options.Exit -> exit 0
    | Options.CreateAccount ->
        let allowBaseAccountWithAllCurrencies = (numAccounts = 0)
        let specificCurrency = UserInteraction.AskCurrency allowBaseAccountWithAllCurrencies
        let password = UserInteraction.AskPassword true
        match specificCurrency with
        | Some(currency) ->
            let account = Async.RunSynchronously (Account.CreateNormalAccount currency password None)
            Console.WriteLine("Account created: " + (account:>IAccount).PublicAddress)
        | None ->
            Async.RunSynchronously (Account.CreateBaseAccount password) |> ignore
            Console.WriteLine("Base accounts created")
        UserInteraction.PressAnyKeyToContinue()
    | Options.Refresh -> ()
    | Options.SendPayment ->
        SendPayment()
    | Options.AddReadonlyAccount ->
        AddReadOnlyAccount()
    | Options.SignOffPayment ->
        SignOffPayment()
    | Options.BroadcastPayment ->
        BroadcastPayment()
    | Options.ArchiveAccount ->
        ArchiveAccount()
    | _ -> failwith "Unreachable"

let rec GetAccountOfSameCurrency currency =
    let account = UserInteraction.AskAccount()
    if (account.Currency <> currency) then
        Presentation.Error (sprintf "The account selected doesn't match the currency %A" currency)
        GetAccountOfSameCurrency currency
    else
        account

let rec CheckArchivedAccountsAreEmpty(): bool =
    let archivedAccountsInNeedOfAction = Account.GetArchivedAccountsWithPositiveBalance() |> Async.RunSynchronously
    for archivedAccount,balance in archivedAccountsInNeedOfAction do
        let currency = (archivedAccount:>IAccount).Currency
        Console.WriteLine (sprintf "ALERT! An archived account has received funds:%sAddress: %s Balance: %s%A"
                               Environment.NewLine
                               (archivedAccount:>IAccount).PublicAddress
                               (balance.ToString())
                               currency)
        Console.WriteLine "Please indicate the account you would like to transfer the funds to."
        let account = GetAccountOfSameCurrency currency

        let maybeFee = UserInteraction.AskFee archivedAccount (TransferAmount(balance, 0m)) account.PublicAddress
        match maybeFee with
        | None -> ()
        | Some(feeInfo) ->
            let txId =
                Account.SweepArchivedFunds archivedAccount balance account feeInfo
                    |> Async.RunSynchronously
            Console.WriteLine(sprintf "Transaction successful, its ID is:%s%s" Environment.NewLine txId)
            UserInteraction.PressAnyKeyToContinue ()

    not (archivedAccountsInNeedOfAction.Any())

let rec ProgramMainLoop() =
    let accounts = Account.GetAllActiveAccounts()
    UserInteraction.DisplayAccountStatuses(WhichAccount.All(accounts))
    if CheckArchivedAccountsAreEmpty() then
        PerformOptions(accounts.Count())
    ProgramMainLoop()

[<EntryPoint>]
let main argv =

    Infrastructure.SetupSentryHook ()

    let exitCode =
        try
            ProgramMainLoop ()
            0
        with
        | ex ->
            Infrastructure.Report ex
            1

    exitCode
