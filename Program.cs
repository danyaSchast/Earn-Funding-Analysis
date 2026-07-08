using Bybit.Net.Clients;
using CryptoExchange.Net.Interfaces.Clients;
using System.Globalization;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Npgsql;
using Dapper;
using Telegram.Bot.Types.ReplyMarkups;

double spotFee = 0.1;
double hedgeShortFee = 0.036;

DotNetEnv.Env.Load();

var restClient = new BybitRestClient();
string connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
using var connection = new NpgsqlConnection(connectionString);

using var cts = new CancellationTokenSource();
var bot = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN"), cancellationToken: cts.Token);



var bigPercent = (await restClient.V5Api.Earn.GetProductInfoAsync(Bybit.Net.Enums.EarnCategory.FlexibleSaving)).Data.List.
    Where(p => double.Parse(p.EstimateApr.TrimEnd('%'), CultureInfo.InvariantCulture) > 40).ToList();

bigPercent = bigPercent.OrderByDescending(p => double.Parse(p.EstimateApr.TrimEnd('%'), CultureInfo.InvariantCulture)).ToList();

await connection.ExecuteAsync("TRUNCATE TABLE earn_data");

foreach (var product in bigPercent)
{
    
    var tickerResult = await restClient.V5Api.ExchangeData.GetLinearInverseTickersAsync(
    Bybit.Net.Enums.Category.Linear, product.Asset + "USDT");

    if (!tickerResult.Success)
    {
        Console.WriteLine($"Product: {product.Asset}, ticker is absent ({tickerResult.Error})");
        Console.WriteLine();
        continue;    
    }
    var ticker = tickerResult.Data.List.FirstOrDefault();

    var diff = ticker.NextFundingTime.HasValue ? ticker.NextFundingTime.Value - DateTime.UtcNow : TimeSpan.Zero;
    double hoursLeft = diff.TotalHours <= 0 ? 24 : diff.TotalHours;

    double profit = double.Parse(product.EstimateApr.TrimEnd('%'), CultureInfo.InvariantCulture) / 365 / 24 * hoursLeft
        - spotFee - hedgeShortFee
        + (double)(ticker.FundingRate ?? 0) * 100;

    Console.WriteLine($"Coin: {product.Asset}, Estimate APR: {product.EstimateApr}, Current funding: {ticker?.FundingRate * 100}, " +
        $"Next funding update time: {ticker?.NextFundingTime.Value.Hour + 3}"+ ":00" + $"\nPotential Profit : {profit}" + "%");
    Console.WriteLine();

    await connection.ExecuteAsync(
    "INSERT INTO earn_data (asset, estimate_apr, funding_rate, next_funding_time) VALUES (@Asset, @EstimateApr, @FundingRate, @NextFundingTime)",
    new { Asset = product.Asset,
        EstimateApr = double.Parse(product.EstimateApr.TrimEnd('%'), CultureInfo.InvariantCulture),
        FundingRate = ticker?.FundingRate*100,
        NextFundingTime = ticker?.NextFundingTime});

        
}




Console.WriteLine($"@{bot.GetMyName} is running... Press Enter to terminate");
Console.ReadLine();
cts.Cancel();

async Task OnMessage(Message msg, UpdateType type)
{
    if (msg.Text == "/start")
    {
        await bot.SendMessage(msg.Chat, "Цей бот розроблений для визначення можливості заробітку на стейкінгу(депозит під відсотки) криптовалюти на біржі \"ByBit\"." +
            "\nБот рахує кінцевий прибуток, ураховуючи APR(Annual Percentage Rate - Річна Відсоткова Ставка), час, на який відкритий стейкінг(прибуток нараховується щогодинно)," +
            " комісію за відкриття та утримання позиції.\nЩодо результатів - так, зазвичай прибуток буде від'ємний, проте бот я створював заради відслідковування прибуткових" +
            " ситуацій, в яких APR може сягати 500%.\nПропоную переглянути можливі стейкінги з привабливими відсотками командою \"/availableEarn\"");
    }
    if (msg.Text == "/availableEarn")
    {
        var availabreEarn = (await restClient.V5Api.Earn.GetProductInfoAsync(Bybit.Net.Enums.EarnCategory.FlexibleSaving)).Data.List.
            Where(a => double.Parse(a.EstimateApr.TrimEnd('%'), CultureInfo.InvariantCulture) > 40).ToList();

        availabreEarn = availabreEarn.OrderByDescending(a => double.Parse(a.EstimateApr.TrimEnd('%'), CultureInfo.InvariantCulture)).ToList();

        foreach (var product in availabreEarn)
        {
            await bot.SendMessage(msg.Chat, $"Coin: {product.Asset}, Current APR: {product.EstimateApr}",
                replyMarkup: new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData($"Розрахувати прибуток",product.Asset)));
        }
    }
}

async Task OnUpdate(Update update)
{
    if (update is { CallbackQuery: { } query })
    {
        var asset = query.Data;
        await bot.AnswerCallbackQuery(query.Id);

        var coin = await connection.QueryFirstOrDefaultAsync<CoinInfo>(
    @"SELECT asset AS ""Asset"", 
             estimate_apr AS ""EstimateApr"", 
             funding_rate AS ""FundingRate"", 
             next_funding_time AS ""NextFundingTime"" 
      FROM earn_data WHERE asset = @Asset",
    new { Asset = asset });

        if (coin == null)
        {
            await bot.SendMessage(query.Message.Chat, $"Нет данных по {asset} в базе");
            return;
        }
        var diff = coin.NextFundingTime.HasValue ? coin.NextFundingTime.Value - DateTime.UtcNow.AddHours(3) : TimeSpan.Zero;
        double hoursLeft = diff.TotalHours <= 0 ? 24 : Math.Floor(diff.TotalHours);

        double profit = coin.EstimateApr / 365 / 24 * hoursLeft
        - spotFee - hedgeShortFee
        + (double)(coin.FundingRate ?? 0);

        await bot.SendMessage(query.Message.Chat, $"Прибуток від позиції {asset}: {profit:F2}%");
    }
}

public class CoinInfo
{
    public string Asset { get; set; }
    public double EstimateApr { get; set; }
    public decimal? FundingRate { get; set; }
    public DateTime? NextFundingTime { get; set; }
}