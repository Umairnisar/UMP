
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UMB.Api.Services.Integrations;
using UMB.Model.Models;

namespace UMB.Api.Services
{
    public class AutoReplyService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<AutoReplyService> _logger;
        private readonly TimeSpan _pollInterval;

        public AutoReplyService(
            IServiceProvider serviceProvider,
            IConfiguration config,
            ILogger<AutoReplyService> logger)
        {
            _serviceProvider = serviceProvider;
            _config = config;
            _logger = logger;
            _pollInterval = TimeSpan.FromSeconds(config.GetValue<int>("AutoReplySettings:PollIntervalSeconds", 30));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessNewMessagesAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing auto-replies");
                }
                await Task.Delay(_pollInterval, stoppingToken);
            }
        }

        private async Task ProcessNewMessagesAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var linkedinService = scope.ServiceProvider.GetRequiredService<ILinkedInIntegrationService>();
            var gmailService = scope.ServiceProvider.GetRequiredService<IGmailIntegrationService>();
            var whatsappService = scope.ServiceProvider.GetRequiredService<IWhatsAppIntegrationService>();
            var outlookService = scope.ServiceProvider.GetRequiredService<IOutlookIntegrationService>();



            var newMessages = await dbContext.MessageMetadatas
                .Where(m => m.IsNew && !m.IsAutoReplied && m.From != "You")
                .ToListAsync(stoppingToken);

            foreach (var message in newMessages)
            {
                try
                {
                    string replyText = _config[$"AutoReplySettings:{message.PlatformType}:Template"] ?? "Thank you for your message! I'll get back to you soon.";
                    string recipient = message.FromEmail;
                    string subject = message.PlatformType == "Gmail" ? $"Re: {message.Subject}" : "Auto-Reply";

                    switch (message.PlatformType)
                    {
                        case "LinkedIn":
                            await linkedinService.SendMessageAsync(
                                message.UserId,
                                recipient, // Assumes recipient is entityUrn
                                replyText,
                                message.AccountIdentifier);
                            break;
                        case "Outlook":
                            await outlookService.SendMessageAsync(
                                message.UserId,
                                subject,
                                replyText,
                                recipient, // Assumes recipient is entityUrn
                                message.AccountIdentifier);
                            break;
                        case "Gmail":
                            await gmailService.SendMessageAsync(
                                message.UserId,
                                subject,
                                replyText,
                                recipient,
                                message.AccountIdentifier);
                            break;
                        case "WhatsApp":
                            await whatsappService.SendMessageAsync(
                                 message.UserId,
                                recipient,
                                replyText,
                                message.AccountIdentifier);
                            break;
                        default:
                            _logger.LogWarning("Unsupported platform {PlatformType} for auto-reply", message.PlatformType);
                            continue;
                    }

                    message.IsAutoReplied = true;
                    message.IsNew = false;
                    dbContext.MessageMetadatas.Update(message);
                    await dbContext.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation("Sent auto-reply for {PlatformType} message {ExternalMessageId} from {AccountIdentifier}",
                        message.PlatformType, message.ExternalMessageId, message.AccountIdentifier);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send auto-reply for {PlatformType} message {ExternalMessageId} from {AccountIdentifier}",
                        message.PlatformType, message.ExternalMessageId, message.AccountIdentifier);
                }
            }
        }
    }
}