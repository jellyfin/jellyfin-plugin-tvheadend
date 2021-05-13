const TVHclientConfigurationPageVar = {
    pluginUniqueId: '3fd018e5-5e78-4e58-b280-a0c068febee0'
};

export default function (view, params) {
    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        const page = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            page.querySelector('#txtTVH_ServerName').value = config.TVH_ServerName || '';
            page.querySelector('#txtHTTP_Port').value = config.HTTP_Port || '9981';
            page.querySelector('#txtHTSP_Port').value = config.HTSP_Port || '9982';
            page.querySelector('#txtWebRoot').value = config.WebRoot || '/';
            page.querySelector('#txtUserName').value = config.Username || '';
            page.querySelector('#txtPassword').value = config.Password || '';
            page.querySelector('#txtPriority').value = config.Priority || '5';
            page.querySelector('#txtProfile').value = config.Profile || '';
            page.querySelector('#selChannelType').value = config.ChannelType || 'Ignore';
            page.querySelector('#chkHideRecordingsChannel').checked = config.HideRecordingsChannel || false;
            page.querySelector('#chkEnableSubsMaudios').checked = config.EnableSubsMaudios || false;
            page.querySelector('#chkForceDeinterlace').checked = config.ForceDeinterlace || false;
            Dashboard.hideLoadingMsg();
        });
    });
    view.querySelector('.TVHclientConfigurationForm').addEventListener('submit', function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        const form = this;
        ApiClient.getPluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId).then(function(config) {
            config.TVH_ServerName = form.querySelector('#txtTVH_ServerName').value;
            config.HTTP_Port = form.querySelector('#txtHTTP_Port').value;
            config.HTSP_Port = form.querySelector('#txtHTSP_Port').value;
            config.WebRoot = form.querySelector('#txtWebRoot').value;
            config.Username = form.querySelector('#txtUserName').value;
            config.Password = form.querySelector('#txtPassword').value;
            config.Priority = form.querySelector('#txtPriority').value;
            config.Profile = form.querySelector('#txtProfile').value;
            config.ChannelType = form.querySelector('#selChannelType').value;
            config.HideRecordingsChannel = form.querySelector('#chkHideRecordingsChannel').checked;
            config.EnableSubsMaudios = form.querySelector('#chkEnableSubsMaudios').checked;
            config.ForceDeinterlace = form.querySelector('#chkForceDeinterlace').checked;
            ApiClient.updatePluginConfiguration(TVHclientConfigurationPageVar.pluginUniqueId, config).then(Dashboard.processPluginConfigurationUpdateResult);
        });
        return false;
    });
}
