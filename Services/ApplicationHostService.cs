using LittleFancyToolAva.Models;
using Microsoft.Extensions.Logging;
using Serilog;

namespace LittleFancyToolAva.Services
{
    public class ApplicationHostService
    {
        private readonly FileService _fileService;
        private readonly AppObserveModel _appObserveModel;
        private readonly IViewStateService _viewStateService;
        private readonly ILogger<ApplicationHostService> _logger;

        public ApplicationHostService(
            FileService fileService,
            AppObserveModel appObserveModel,
            IViewStateService viewStateService,
            ILogger<ApplicationHostService> logger)
        {
            _fileService = fileService;
            _appObserveModel = appObserveModel;
            _viewStateService = viewStateService;
            _logger = logger;
        }

        public void LoadState()
        {
            try
            {
                var state = _fileService.LoadState();
                if (state?.Preferences != null)
                {
                    _appObserveModel.Preferences = state.Preferences;
                    _logger.LogInformation("Preferences loaded successfully");
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Preferences load IO error");
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "Preferences JSON corrupt, using defaults");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Preferences access denied, using defaults");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error loading preferences");
            }
        }

        public void LoadViewStates()
        {
            try
            {
                _viewStateService.LoadAll();
                _logger.LogInformation("View states loaded successfully");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "View states load IO error");
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "View states JSON corrupt, using defaults");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "View states access denied");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error loading view states");
            }
        }

        public void SaveState()
        {
            try
            {
                _fileService.SaveState(_appObserveModel);
                _viewStateService.SaveAll();
                _logger.LogInformation("State saved successfully");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "State save IO error");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "State save access denied");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error saving state");
            }
        }
    }
}
