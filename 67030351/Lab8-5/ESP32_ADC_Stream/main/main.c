#include <stdio.h>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_err.h"
#include "esp_adc/adc_oneshot.h"

#if CONFIG_IDF_TARGET_ESP32C6
#define POT_ADC_CHANNEL ADC_CHANNEL_4  // GPIO 4 บน ESP32-C6
#else
#define POT_ADC_CHANNEL ADC_CHANNEL_6  // GPIO 34 บน ESP32 Classic
#endif

void app_main(void)
{
    printf("\n[SYSTEM] ESP32 ADC Stream for Lab 8.5 Dual-Channel starting...\n");

    adc_oneshot_unit_handle_t adc1_handle;
    adc_oneshot_unit_init_cfg_t init_config = {
        .unit_id = ADC_UNIT_1,
        .ulp_mode = ADC_ULP_MODE_DISABLE,
    };
    ESP_ERROR_CHECK(adc_oneshot_new_unit(&init_config, &adc1_handle));

    adc_oneshot_chan_cfg_t channel_config = {
        .bitwidth = ADC_BITWIDTH_12,
        .atten = ADC_ATTEN_DB_12,
    };
    ESP_ERROR_CHECK(adc_oneshot_config_channel(
        adc1_handle, POT_ADC_CHANNEL, &channel_config));

    int raw_value = 0;
    float filtered_value = 0.0f;
    const float alpha = 0.25f;

    while (1) {
        ESP_ERROR_CHECK(adc_oneshot_read(
            adc1_handle, POT_ADC_CHANNEL, &raw_value));

        filtered_value = (alpha * raw_value) +
                         ((1.0f - alpha) * filtered_value);

        printf("%d\n", (int)filtered_value);
        vTaskDelay(pdMS_TO_TICKS(50));
    }
}
