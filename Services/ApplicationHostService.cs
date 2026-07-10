using LittleFancyToolAva.Models;
using System;
using System.Diagnostics;

namespace LittleFancyToolAva.Services
{
    public class ApplicationHostService
    {
        private readonly FileService _fileService;
        private readonly AppObserveModel _appObserveModel;

        public ApplicationHostService(FileService fileService, AppObserveModel appObserveModel)
        {
            _fileService = fileService;
            _appObserveModel = appObserveModel;
        }

        public void LoadState()
        {
            try
            {
                var state = _fileService.LoadState();
                if (state?.Preferences != null)
                {
                    _appObserveModel.Preferences = state.Preferences;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppHost] Load failed: {ex.Message}");
            }
        }

        public void SaveState()
        {
            try
            {
                _fileService.SaveState(_appObserveModel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppHost] Save failed: {ex.Message}");
            }
        }
    }
}
